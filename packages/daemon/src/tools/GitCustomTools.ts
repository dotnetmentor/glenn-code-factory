// Agent-facing git workflow tools (daemon-tools MCP).
//
// Remote git runs through GitModule so GitHub App installation tokens,
// the sequential queue, and audit fan-out stay consistent with auto-commit.
// File-level conflict resolution stays on Cursor read/write/edit tools.

import type { Logger } from 'pino'

import type { GitModule, TurnCtx } from '../git/GitModule.js'
import type { CustomTool, ToolContext, ToolResult } from '../turn/types.js'

export type GitModuleAccessor = () => GitModule | undefined

function turnCtx(ctx: ToolContext): TurnCtx {
  return { conversationId: ctx.sessionId, turnId: ctx.turnId }
}

function unavailable(): ToolResult {
  return {
    ok: false,
    error:
      'Git is not available on this runtime (no repo wired yet). Try again after bootstrap completes.',
  }
}

function resolveGit(getGitModule: GitModuleAccessor): GitModule | null {
  return getGitModule() ?? null
}

const gitStatusSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {},
  additionalProperties: false,
} as const

const gitSyncSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {
    branch: {
      type: 'string',
      description:
        'Branch to fast-forward with origin. Defaults to the current branch when omitted.',
    },
  },
  additionalProperties: false,
} as const

const gitStartMergeSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {
    branch: {
      type: 'string',
      description:
        'Branch to merge into the current HEAD (e.g. main or origin/main after sync).',
    },
  },
  required: ['branch'],
  additionalProperties: false,
} as const

const gitCompleteMergeSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {
    paths: {
      type: 'array',
      items: { type: 'string' },
      description:
        'Resolved file paths to stage. Omit to stage all tracked changes (git add -u).',
    },
  },
  additionalProperties: false,
} as const

const gitAbortMergeSchema = gitStatusSchema

const GIT_TOOL_PREAMBLE =
  'Uses daemon GitHub auth and serializes with auto-commit. Do not use shell git for fetch/merge/push on this runtime. ' +
  'Resolve conflict markers with read/edit, then git_complete_merge. Normal turns: do not hand-roll commit/push — the harness handles that after idle.\n\n'

export function buildGitCustomTools(deps: {
  logger: Logger
  getGitModule: GitModuleAccessor
}): CustomTool[] {
  const childLogger = deps.logger.child({ module: 'git-custom-tools' })

  return [
    {
      name: 'git_status',
      description:
        GIT_TOOL_PREAMBLE +
        'Read branch, whether a merge is in progress, conflicted paths, and porcelain status.',
      inputSchema: gitStatusSchema,
      async run(_args: unknown, ctx: ToolContext): Promise<ToolResult> {
        const git = resolveGit(deps.getGitModule)
        if (git === null) return unavailable()
        try {
          const status = await git.getWorkflowStatus()
          return { ok: true, ...status }
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err)
          childLogger.warn({ err }, 'git_status failed')
          return { ok: false, error: message }
        }
      },
    },
    {
      name: 'git_sync_with_origin',
      description:
        GIT_TOOL_PREAMBLE +
        'Fetch and fast-forward the current branch with origin (no rebase). Fails when histories diverged — resolve manually or merge instead.',
      inputSchema: gitSyncSchema,
      async run(args: unknown, ctx: ToolContext): Promise<ToolResult> {
        const git = resolveGit(deps.getGitModule)
        if (git === null) return unavailable()
        const branch =
          args !== null &&
          typeof args === 'object' &&
          'branch' in args &&
          typeof (args as { branch?: unknown }).branch === 'string'
            ? (args as { branch: string }).branch
            : undefined
        try {
          return await git.syncWithOrigin(branch, turnCtx(ctx))
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err)
          childLogger.warn({ err, branch }, 'git_sync_with_origin failed')
          return { ok: false, error: message }
        }
      },
    },
    {
      name: 'git_start_merge',
      description:
        GIT_TOOL_PREAMBLE +
        'Start `git merge --no-ff <branch>` and leave conflict markers in the tree (does not auto-abort). ' +
        'Then fix files and call git_complete_merge, or git_abort_merge to cancel.',
      inputSchema: gitStartMergeSchema,
      async run(args: unknown, ctx: ToolContext): Promise<ToolResult> {
        const git = resolveGit(deps.getGitModule)
        if (git === null) return unavailable()
        if (args === null || typeof args !== 'object') {
          return { ok: false, error: 'invalid input: args must be an object' }
        }
        const branch = (args as { branch?: unknown }).branch
        if (typeof branch !== 'string' || branch.trim().length === 0) {
          return { ok: false, error: 'invalid input: branch is required' }
        }
        try {
          return await git.mergeLeaveConflicts(branch.trim(), turnCtx(ctx))
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err)
          childLogger.warn({ err, branch }, 'git_start_merge failed')
          return { ok: false, error: message }
        }
      },
    },
    {
      name: 'git_complete_merge',
      description:
        GIT_TOOL_PREAMBLE +
        'Stage resolved paths and run `git merge --continue`. Only valid while a merge is in progress.',
      inputSchema: gitCompleteMergeSchema,
      async run(args: unknown, ctx: ToolContext): Promise<ToolResult> {
        const git = resolveGit(deps.getGitModule)
        if (git === null) return unavailable()
        let paths: string[] | undefined
        if (args !== null && typeof args === 'object' && 'paths' in args) {
          const raw = (args as { paths?: unknown }).paths
          if (raw !== undefined) {
            if (!Array.isArray(raw) || raw.some((p) => typeof p !== 'string')) {
              return { ok: false, error: 'invalid input: paths must be an array of strings' }
            }
            paths = raw
          }
        }
        try {
          return await git.completeMerge(paths, turnCtx(ctx))
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err)
          childLogger.warn({ err }, 'git_complete_merge failed')
          return { ok: false, error: message }
        }
      },
    },
    {
      name: 'git_abort_merge',
      description:
        GIT_TOOL_PREAMBLE + 'Abort an in-progress merge (`git merge --abort`) when MERGE_HEAD exists.',
      inputSchema: gitAbortMergeSchema,
      async run(_args: unknown, ctx: ToolContext): Promise<ToolResult> {
        const git = resolveGit(deps.getGitModule)
        if (git === null) return unavailable()
        try {
          return await git.abortMerge(turnCtx(ctx))
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err)
          childLogger.warn({ err }, 'git_abort_merge failed')
          return { ok: false, error: message }
        }
      },
    },
  ]
}
