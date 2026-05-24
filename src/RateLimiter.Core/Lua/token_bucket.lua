-- Token bucket rate limiter (atomic).
-- KEYS[1]  bucket key (a hash with fields: tokens, ts)
-- ARGV[1]  capacity            (max tokens / max burst)
-- ARGV[2]  refill_per_ms       (tokens added per millisecond)
-- ARGV[3]  now_ms              (caller clock, ms since epoch)
-- ARGV[4]  permits             (tokens this request wants)
-- returns  { allowed(0|1), remaining, retry_after_ms }

local capacity      = tonumber(ARGV[1])
local refill_per_ms = tonumber(ARGV[2])
local now           = tonumber(ARGV[3])
local permits       = tonumber(ARGV[4])

local state  = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
local tokens = tonumber(state[1])
local ts     = tonumber(state[2])

if tokens == nil then
  tokens = capacity
  ts = now
end

-- Refill based on elapsed time, capped at capacity.
local elapsed = now - ts
if elapsed < 0 then elapsed = 0 end
tokens = math.min(capacity, tokens + (elapsed * refill_per_ms))
ts = now

local allowed = 0
if tokens >= permits then
  tokens = tokens - permits
  allowed = 1
end

redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', ts)

-- Expire the key once it would be fully refilled to keep idle keys from lingering.
local ttl_ms = math.ceil((capacity - tokens) / refill_per_ms)
if ttl_ms < 1 then ttl_ms = 1 end
redis.call('PEXPIRE', KEYS[1], ttl_ms)

local retry_after = 0
if allowed == 0 then
  retry_after = math.ceil((permits - tokens) / refill_per_ms)
end

return { allowed, math.floor(tokens), retry_after }
