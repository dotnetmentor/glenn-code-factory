#!/usr/bin/env node
/**
 * Platform auth helpers for publish/bootstrap scripts.
 *
 * Reads secrets from SystemSettings (decrypting with SystemSettings__EncryptionKey
 * from .env) and mints a SuperAdmin JWT (with Jwt__Key + bootstrap user from DB).
 * No manual FLY_API_TOKEN or /tmp/jwt.txt required when .env + Postgres are set up.
 */
import { createDecipheriv, createHmac, randomUUID } from 'node:crypto'
import { readFileSync, existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

const __dirname = dirname(fileURLToPath(import.meta.url))
const REPO_ROOT = join(__dirname, '..', '..')

const NONCE_SIZE = 12
const TAG_SIZE = 16
const KEY_SIZE = 32

const DEFAULT_PG_URL = 'postgresql://postgres@localhost:43594/app'
const DEFAULT_JWT_ISSUER = 'orchestrator-api'
const DEFAULT_JWT_AUDIENCE = 'orchestrator-api'
const DEFAULT_JWT_EXPIRY_MINUTES = 43200

export function loadEnv(repoRoot = REPO_ROOT) {
  const envPath = join(repoRoot, '.env')
  if (!existsSync(envPath)) return

  for (const line of readFileSync(envPath, 'utf8').split('\n')) {
    const trimmed = line.trim()
    if (!trimmed || trimmed.startsWith('#')) continue
    const eq = trimmed.indexOf('=')
    if (eq <= 0) continue
    const key = trimmed.slice(0, eq).trim()
    let value = trimmed.slice(eq + 1).trim()
    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1)
    }
    if (process.env[key] === undefined) {
      process.env[key] = value
    }
  }
}

function psqlQuery(pgUrl, sql) {
  const result = spawnSync('psql', [pgUrl, '-tA', '-c', sql], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  if (result.status !== 0) {
    const err = (result.stderr || result.stdout || '').trim()
    throw new Error(`psql failed: ${err || 'unknown error'}`)
  }
  return (result.stdout || '').trim()
}

function getEncryptionKey() {
  const raw = process.env.SystemSettings__EncryptionKey
  if (!raw) {
    throw new Error(
      'SystemSettings__EncryptionKey is not set. Add it to .env (openssl rand -base64 32).',
    )
  }
  const key = Buffer.from(raw, 'base64')
  if (key.length !== KEY_SIZE) {
    throw new Error(
      `SystemSettings__EncryptionKey must decode to ${KEY_SIZE} bytes (got ${key.length}).`,
    )
  }
  return key
}

function getJwtKey() {
  const raw = process.env.Jwt__Key
  if (!raw || raw.length < 32) {
    throw new Error('Jwt__Key is not set or too short. Add it to .env (min 32 chars).')
  }
  return raw
}

export function decryptSystemSetting(cipherBase64, encryptionKey = getEncryptionKey()) {
  const combined = Buffer.from(cipherBase64, 'base64')
  if (combined.length < NONCE_SIZE + TAG_SIZE) {
    throw new Error('Stored SystemSetting value is too short to contain nonce + tag.')
  }

  const nonce = combined.subarray(0, NONCE_SIZE)
  const tag = combined.subarray(combined.length - TAG_SIZE)
  const ciphertext = combined.subarray(NONCE_SIZE, combined.length - TAG_SIZE)

  const decipher = createDecipheriv('aes-256-gcm', encryptionKey, nonce)
  decipher.setAuthTag(tag)
  return Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString('utf8')
}

export function readSystemSetting(key, { pgUrl = process.env.PG_URL || DEFAULT_PG_URL } = {}) {
  const escaped = key.replace(/'/g, "''")
  const row = psqlQuery(
    pgUrl,
    `SELECT "Value", "IsSecret" FROM "SystemSettings" WHERE "Key"='${escaped}' LIMIT 1;`,
  )
  if (!row) {
    throw new Error(`SystemSettings row not found: ${key}`)
  }

  const sep = row.indexOf('|')
  if (sep === -1) {
    throw new Error(`Unexpected SystemSettings row format for ${key}`)
  }

  const value = row.slice(0, sep)
  const isSecret = row.slice(sep + 1) === 't'
  if (value === '' || value === 'null') {
    throw new Error(`SystemSettings.${key} has no value`)
  }

  return isSecret ? decryptSystemSetting(value) : value
}

function base64Url(input) {
  return Buffer.from(input)
    .toString('base64')
    .replace(/=/g, '')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
}

function base64UrlJson(obj) {
  return base64Url(JSON.stringify(obj))
}

export function mintSuperAdminJwt({
  pgUrl = process.env.PG_URL || DEFAULT_PG_URL,
  email = process.env.Bootstrap__SuperAdminEmail,
  jwtKey = getJwtKey(),
  issuer = process.env.Jwt__Issuer || DEFAULT_JWT_ISSUER,
  audience = process.env.Jwt__Audience || DEFAULT_JWT_AUDIENCE,
  expiryMinutes = Number(process.env.Jwt__ExpiryMinutes || DEFAULT_JWT_EXPIRY_MINUTES),
} = {}) {
  if (!email) {
    throw new Error('Bootstrap__SuperAdminEmail is not set in .env')
  }

  const escapedEmail = email.replace(/'/g, "''")
  const userRow = psqlQuery(
    pgUrl,
    `SELECT "Id", "Email", COALESCE("FirstName", ''), COALESCE("LastName", '') FROM "AspNetUsers" WHERE "Email"='${escapedEmail}' LIMIT 1;`,
  )
  if (!userRow) {
    throw new Error(`Bootstrap SuperAdmin user not found: ${email}`)
  }

  const parts = userRow.split('|')
  const [userId, userEmail, firstName, lastName] = parts
  const roleRows = psqlQuery(
    pgUrl,
    `SELECT r."Name" FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId" WHERE ur."UserId"='${userId.replace(/'/g, "''")}';`,
  )
  const roles = roleRows ? roleRows.split('\n').filter(Boolean) : []
  if (!roles.includes('SuperAdmin')) {
    throw new Error(`User ${email} does not have SuperAdmin role`)
  }

  const now = Math.floor(Date.now() / 1000)
  const payload = {
    sub: userId,
    email: userEmail,
    jti: randomUUID(),
    iat: now,
    'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier': userId,
    'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress': userEmail,
    iss: issuer,
    aud: audience,
    exp: now + expiryMinutes * 60,
  }

  if (firstName) {
    payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname'] = firstName
  }
  if (lastName) {
    payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname'] = lastName
  }
  const fullName = `${firstName} ${lastName}`.trim()
  if (fullName) {
    payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name'] = fullName
  }

  payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] = roles

  const header = base64UrlJson({ alg: 'HS256', typ: 'JWT' })
  const body = base64UrlJson(payload)
  const signature = base64Url(
    createHmac('sha256', jwtKey).update(`${header}.${body}`).digest(),
  )
  return `${header}.${body}.${signature}`
}

export function readFlyApiToken(options = {}) {
  return readSystemSetting('Fly:ApiToken', options)
}

function main() {
  loadEnv()
  const cmd = process.argv[2]
  try {
    switch (cmd) {
      case 'decrypt': {
        const key = process.argv[3]
        if (!key) throw new Error('usage: platform-auth.mjs decrypt <SystemSettingsKey>')
        process.stdout.write(readSystemSetting(key))
        break
      }
      case 'fly-token':
        process.stdout.write(readFlyApiToken())
        break
      case 'jwt':
        process.stdout.write(mintSuperAdminJwt())
        break
      default:
        console.error('usage: platform-auth.mjs <decrypt <key>|fly-token|jwt>')
        process.exit(2)
    }
  } catch (err) {
    console.error(`platform-auth: ${err.message}`)
    process.exit(1)
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main()
}
