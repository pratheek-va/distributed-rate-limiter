-- Fixed window rate limiter (atomic).
-- The window starts on the first request and expires after `window_ms`.
-- KEYS[1]  counter key
-- ARGV[1]  limit       (max permits per window)
-- ARGV[2]  window_ms   (window length in ms)
-- ARGV[3]  permits     (permits this request wants)
-- returns  { allowed(0|1), remaining, retry_after_ms }

local limit     = tonumber(ARGV[1])
local window_ms = tonumber(ARGV[2])
local permits   = tonumber(ARGV[3])

local current = tonumber(redis.call('GET', KEYS[1]) or '0')
local allowed = 0

if current + permits <= limit then
  current = redis.call('INCRBY', KEYS[1], permits)
  if current == permits then
    -- first write in this window: start the expiry clock
    redis.call('PEXPIRE', KEYS[1], window_ms)
  end
  allowed = 1
end

local remaining = limit - current
if remaining < 0 then remaining = 0 end

local retry_after = 0
if allowed == 0 then
  retry_after = redis.call('PTTL', KEYS[1])
  if retry_after < 0 then retry_after = window_ms end
end

return { allowed, remaining, retry_after }
