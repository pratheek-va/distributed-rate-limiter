-- Sliding window log rate limiter (atomic).
-- Stores one sorted-set entry per permit, scored by timestamp; old entries are trimmed each call.
-- KEYS[1]  sorted set key
-- ARGV[1]  limit       (max permits in the trailing window)
-- ARGV[2]  window_ms   (window length in ms)
-- ARGV[3]  now_ms      (caller clock, ms since epoch)
-- ARGV[4]  permits     (permits this request wants)
-- ARGV[5]  member_id   (unique token so concurrent calls don't collide)
-- returns  { allowed(0|1), remaining, retry_after_ms }

local limit     = tonumber(ARGV[1])
local window_ms = tonumber(ARGV[2])
local now       = tonumber(ARGV[3])
local permits   = tonumber(ARGV[4])
local member_id = ARGV[5]

-- Drop entries that have aged out of the trailing window.
redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, now - window_ms)

local count   = redis.call('ZCARD', KEYS[1])
local allowed = 0

if count + permits <= limit then
  for i = 1, permits do
    redis.call('ZADD', KEYS[1], now, member_id .. '-' .. i)
  end
  count = count + permits
  allowed = 1
end

redis.call('PEXPIRE', KEYS[1], window_ms)

local remaining = limit - count
if remaining < 0 then remaining = 0 end

local retry_after = 0
if allowed == 0 then
  -- time until the oldest in-window entry expires
  local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
  if oldest[2] then
    retry_after = (tonumber(oldest[2]) + window_ms) - now
    if retry_after < 0 then retry_after = 0 end
  end
end

return { allowed, remaining, retry_after }
