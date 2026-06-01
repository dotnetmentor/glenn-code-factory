import { describe, expect, it, vi } from 'vitest'

import type { CustomTool, ToolContext } from '../turn/types.js'
import {
  DAEMON_TOOLS_MCP_PORT,
  DAEMON_TOOLS_SERVER_NAME,
  DaemonToolsMcpServer,
} from './DaemonToolsMcpServer.js'

function makeLogger() {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(),
  }
  log.child.mockReturnValue(log)
  return log
}

async function postJsonRpc(
  port: number,
  body: { jsonrpc: '2.0'; id: number; method: string; params?: unknown },
): Promise<{ status: number; json: unknown }> {
  const res = await fetch(`http://127.0.0.1:${port}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  return { status: res.status, json: await res.json() }
}

describe('DaemonToolsMcpServer', () => {
  it('exposes initialize, tools/list, and tools/call over loopback HTTP JSON-RPC', async () => {
    const run = vi.fn(async () => ({ ok: true, message: 'done' }))
    const tools: CustomTool[] = [
      {
        name: 'propose_runtime_spec',
        description: 'Propose a runtime spec change',
        inputSchema: { type: 'object' },
        run,
      },
      {
        name: 'restart_service',
        description: 'Restart a supervisord service',
        inputSchema: { type: 'object' },
        run: vi.fn(),
      },
    ]

    const server = new DaemonToolsMcpServer({
      tools,
      logger: makeLogger() as unknown as import('pino').Logger,
    })
    await server.start('127.0.0.1', DAEMON_TOOLS_MCP_PORT)

    try {
      const init = await postJsonRpc(DAEMON_TOOLS_MCP_PORT, {
        jsonrpc: '2.0',
        id: 1,
        method: 'initialize',
        params: {},
      })
      expect(init.status).toBe(200)
      expect(init.json).toMatchObject({
        jsonrpc: '2.0',
        id: 1,
        result: {
          serverInfo: { name: DAEMON_TOOLS_SERVER_NAME },
        },
      })

      const list = await postJsonRpc(DAEMON_TOOLS_MCP_PORT, {
        jsonrpc: '2.0',
        id: 2,
        method: 'tools/list',
      })
      expect(list.status).toBe(200)
      const listResult = (list.json as { result: { tools: Array<{ name: string }> } }).result
      expect(listResult.tools.map((t) => t.name).sort()).toEqual([
        'propose_runtime_spec',
        'restart_service',
      ])

      const ctx: ToolContext = {
        signalr: {} as ToolContext['signalr'],
        config: {} as ToolContext['config'],
        sessionId: 'sess-1',
        turnId: 'turn-1',
      }
      server.setTurnContext(ctx)

      const call = await postJsonRpc(DAEMON_TOOLS_MCP_PORT, {
        jsonrpc: '2.0',
        id: 3,
        method: 'tools/call',
        params: { name: 'propose_runtime_spec', arguments: { reason: 'test' } },
      })
      expect(call.status).toBe(200)
      expect(run).toHaveBeenCalledWith({ reason: 'test' }, ctx)
      const callResult = (call.json as { result: { content: Array<{ text: string }> } }).result
      expect(JSON.parse(callResult.content[0]!.text)).toEqual({ ok: true, message: 'done' })
    } finally {
      await server.stop()
    }
  })

  it('tools/call fails when no turn context is set', async () => {
    const tools: CustomTool[] = [
      {
        name: 'restart_service',
        description: 'Restart',
        inputSchema: { type: 'object' },
        run: vi.fn(),
      },
    ]
    const server = new DaemonToolsMcpServer({
      tools,
      logger: makeLogger() as unknown as import('pino').Logger,
    })
    await server.start('127.0.0.1', 4198)
    try {
      const call = await postJsonRpc(4198, {
        jsonrpc: '2.0',
        id: 1,
        method: 'tools/call',
        params: { name: 'restart_service', arguments: {} },
      })
      expect(call.status).toBe(200)
      expect(call.json).toMatchObject({
        error: { code: -32603 },
      })
    } finally {
      await server.stop()
    }
  })
})
