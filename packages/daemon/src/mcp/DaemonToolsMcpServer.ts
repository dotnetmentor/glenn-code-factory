import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'
import type { Logger } from 'pino'

import type { CustomTool, ToolContext } from '../turn/types.js'

export const DAEMON_TOOLS_MCP_PORT = 4199
export const DAEMON_TOOLS_SERVER_NAME = 'glenn-daemon-tools'
const SERVER_VERSION = '0.1.0'

type JsonRpcRequest = {
  jsonrpc?: string
  id?: number | string | null
  method?: string
  params?: unknown
}

type JsonRpcSuccess = {
  jsonrpc: '2.0'
  id: number | string | null
  result: unknown
}

type JsonRpcError = {
  jsonrpc: '2.0'
  id: number | string | null
  error: { code: number; message: string; data?: unknown }
}

export class DaemonToolsMcpServer {
  readonly #tools: readonly CustomTool[]
  readonly #logger: Logger
  #server: Server | undefined
  #turnContext: ToolContext | null = null

  constructor(deps: { tools: readonly CustomTool[]; logger: Logger }) {
    this.#tools = deps.tools
    this.#logger = deps.logger.child({ module: 'daemon-tools-mcp' })
  }

  /**
   * Stamp per-turn context before opening the Cursor agent iterator.
   * Cleared when the turn ends.
   */
  setTurnContext(ctx: ToolContext | null): void {
    this.#turnContext = ctx
  }

  async start(host = '127.0.0.1', port = DAEMON_TOOLS_MCP_PORT): Promise<void> {
    if (this.#server !== undefined) {
      throw new Error('DaemonToolsMcpServer already started')
    }

    this.#server = createServer((req, res) => {
      void this.#handleRequest(req, res)
    })

    await new Promise<void>((resolve, reject) => {
      this.#server!.once('error', reject)
      this.#server!.listen(port, host, () => {
        this.#server!.off('error', reject)
        const addr = this.#server!.address() as AddressInfo
        this.#logger.info(
          { host: addr.address, port: addr.port },
          'daemon-tools MCP server listening',
        )
        resolve()
      })
    })
  }

  async stop(): Promise<void> {
    const server = this.#server
    if (server === undefined) return
    this.#server = undefined
    await new Promise<void>((resolve, reject) => {
      server.close((err) => (err !== undefined ? reject(err) : resolve()))
    })
  }

  async #handleRequest(req: IncomingMessage, res: ServerResponse): Promise<void> {
    if (req.method !== 'POST') {
      res.writeHead(405, { 'Content-Type': 'application/json' })
      res.end(JSON.stringify({ error: 'Method Not Allowed' }))
      return
    }

    let body = ''
    try {
      body = await readBody(req)
    } catch (err) {
      this.#logger.warn({ err }, 'failed to read MCP request body')
      res.writeHead(400, { 'Content-Type': 'application/json' })
      res.end(JSON.stringify({ error: 'Bad Request' }))
      return
    }

    let parsed: JsonRpcRequest
    try {
      parsed = JSON.parse(body) as JsonRpcRequest
    } catch {
      this.#writeJsonRpc(res, {
        jsonrpc: '2.0',
        id: null,
        error: { code: -32700, message: 'Parse error' },
      })
      return
    }

    const id = parsed.id ?? null
    const method = parsed.method
    if (parsed.jsonrpc !== '2.0' || typeof method !== 'string') {
      this.#writeJsonRpc(res, {
        jsonrpc: '2.0',
        id,
        error: { code: -32600, message: 'Invalid Request' },
      })
      return
    }

    try {
      const result = await this.#dispatch(method, parsed.params)
      this.#writeJsonRpc(res, { jsonrpc: '2.0', id, result })
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      this.#writeJsonRpc(res, {
        jsonrpc: '2.0',
        id,
        error: { code: -32603, message },
      })
    }
  }

  async #dispatch(method: string, params: unknown): Promise<unknown> {
    switch (method) {
      case 'initialize':
        return {
          protocolVersion: '2024-11-05',
          capabilities: { tools: {} },
          serverInfo: { name: DAEMON_TOOLS_SERVER_NAME, version: SERVER_VERSION },
        }
      case 'tools/list':
        return {
          tools: this.#tools.map((t) => ({
            name: t.name,
            description: t.description,
            inputSchema: t.inputSchema,
          })),
        }
      case 'tools/call': {
        const ctx = this.#turnContext
        if (ctx === null) {
          throw new Error('No active turn context for tools/call')
        }
        const callParams = params as { name?: unknown; arguments?: unknown }
        const toolName = callParams.name
        if (typeof toolName !== 'string' || toolName === '') {
          throw new Error('tools/call requires a non-empty name')
        }
        const tool = this.#tools.find((t) => t.name === toolName)
        if (tool === undefined) {
          throw new Error(`Unknown tool: ${toolName}`)
        }
        const args =
          callParams.arguments !== null && typeof callParams.arguments === 'object'
            ? callParams.arguments
            : {}
        const result = await tool.run(args, ctx)
        return {
          content: [{ type: 'text', text: JSON.stringify(result) }],
        }
      }
      default:
        throw new Error(`Method not found: ${method}`)
    }
  }

  #writeJsonRpc(res: ServerResponse, payload: JsonRpcSuccess | JsonRpcError): void {
    res.writeHead(200, { 'Content-Type': 'application/json' })
    res.end(JSON.stringify(payload))
  }
}

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = []
    req.on('data', (chunk: Buffer) => chunks.push(chunk))
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')))
    req.on('error', reject)
  })
}
