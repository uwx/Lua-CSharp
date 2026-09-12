-- not based on PUC-Lua tests

print("=== integer fast-path diagnostics ===")
local function chk(name, cond)
  if not cond then error("FAIL: " .. name, 2) end
  print("ok: " .. name)
end

-- arithmetic stays integer
chk("1+1 type", math.type(1 + 1) == "integer")
chk("1+1 val", 1 + 1 == 2)
chk("mixed float", math.type(1 + 1.0) == "float")
chk("div float", math.type(1 / 2) == "float")
chk("7%3 int", math.type(7 % 3) == "integer")
chk("7%3 val", 7 % 3 == 1)
chk("-7%3 val", -7 % 3 == 2)
chk("-3%2 val", -3 % 2 == 1)
chk("mul int", math.type(2 * 3) == "integer")
chk("sub int", math.type(10 - 4) == "integer")
chk("unm int", math.type(-5) == "integer")
-- 2^62 is exact in double and fits in a long; adding it to itself wraps to 2^63 → negative.
local big = 4611686018427387904
chk("overflow wraps neg", big + big < 0)
chk("overflow wraps int", math.type(big + big) == "integer")

-- floor/ceil return integer
chk("floor int", math.type(math.floor(4.5)) == "integer")
chk("floor val", math.floor(4.5) == 4)
chk("ceil int", math.type(math.ceil(4.5)) == "integer")
chk("ceil val", math.ceil(4.5) == 5)

-- length returns integer
chk("# int", math.type(#{1, 2, 3}) == "integer")
chk("# val", #{1, 2, 3} == 3)

-- integer for-loop
local sum = 0
for i = 1, 10 do
  chk("loop var int", math.type(i) == "integer")
  sum = sum + i
end
chk("loop sum", sum == 55)

-- integer loop with # limit
local t = {10, 20, 30}
local acc = 0
for i = 1, #t do
  acc = acc + t[i]
end
chk("# limit loop", acc == 60)

-- integer loop with floor limit and negative step
local cnt = 0
for i = math.floor(5.7), 1, -2 do
  chk("step loop int", math.type(i) == "integer")
  cnt = cnt + 1
end
chk("step loop count", cnt == 3)  -- 5, 3, 1

-- comparison
chk("lt int", 3 < 4)
chk("le int", 4 <= 4)
chk("lt mixed", 3 < 4.0)
chk("le mixed", 4.0 <= 4)

-- integer literal precision (>2^53 no longer lossy via double round-trip)
chk("max long int", math.type(9223372036854775807) == "integer")
chk("max long exact", 9223372036854775807 == 9223372036854775807)
chk("max-1", 9223372036854775807 - 1 == 9223372036854775806)
chk("max+1 wraps", 9223372036854775807 + 1 == -9223372036854775808)
chk("neg max long", math.type(-9223372036854775807) == "integer")
chk("neg max val", -9223372036854775807 == -9223372036854775807)
-- 2^63 and beyond denote floats (Lua 5.3)
chk("2^63 float", math.type(9223372036854775808) == "float")
chk("neg 2^63 float", math.type(-9223372036854775808) == "float")

-- hex integer literals
chk("hex max int", math.type(0x7FFFFFFFFFFFFFFF) == "integer")
chk("hex max val", 0x7FFFFFFFFFFFFFFF == 9223372036854775807)
chk("hex 2^63 float", math.type(0x8000000000000000) == "float")
chk("hex 2^64-1 float", math.type(0xFFFFFFFFFFFFFFFF) == "float")
chk("hex E digit int", math.type(0xE) == "integer")
chk("hex E digit val", 0xE == 14)
chk("hex e digit int", math.type(0x1e) == "integer")
chk("hex e digit val", 0x1e == 30)
chk("hex deadbeef", 0xdeadbeef == 3735928559)
chk("hex mixed +", 0xE + 1 == 15)

-- constant folding stays integer & exact
chk("fold neg int", math.type(-(5 + 5)) == "integer")
chk("fold neg val", -(5 + 5) == -10)
chk("fold mul int", math.type(2 * 3) == "integer")
chk("fold big sub", math.type(9223372036854775807 - 1) == "integer")
chk("fold big sub val", 9223372036854775807 - 1 == 9223372036854775806)
chk("div float", math.type(4 / 2) == "float")
chk("pow float", math.type(2 ^ 3) == "float")
print("=== done ===")
