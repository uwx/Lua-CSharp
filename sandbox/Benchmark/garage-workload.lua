-- garage-workload.lua
-- Models the React-style reconciler hot loop that dominates the garage profile:
-- VNode/table construction, string-keyed field access, pairs iteration, and
-- diffProps-style equality comparisons (mixed-type, primitive, string, and
-- enum-style __eq via a shared metatable).

local EnumMT = {}
EnumMT.__eq = function(a, b)
    return a.value == b.value
end

local function Enum(value)
    return setmetatable({ value = value }, EnumMT)
end

local Key = {
    left = Enum(37),
    right = Enum(39),
    enter = Enum(13),
    escape = Enum(27),
}

local function buildVNodes(n)
    local nodes = {}
    for i = 1, n do
        nodes[i] = {
            name = "carcard " .. tostring(i),
            key = "car-" .. tostring(i),
            style = { width = 280, padding = 24, flexDirection = "column" },
            selected = (i % 2) == 0,
        }
    end
    return nodes
end

local function countPairs(t)
    local c = 0
    for _, _ in pairs(t) do
        c = c + 1
    end
    return c
end

local props = { name = "glasscard", style = "merged", selected = true, hovered = false }

local function run()
    local total = 0
    for iter = 1, 20 do
        local nodes = buildVNodes(150)
        total = total + countPairs(nodes)
        total = total + countPairs(props)

        for i = 1, 150 do
            local node = nodes[i]
            local prev = nodes[(i % 150) + 1]

            -- diffProps-style reference/primitive comparisons
            if node.selected ~= prev.selected then
                total = total + 1
            end
            if node.name ~= prev.name then
                total = total + 1
            end
            if node.style ~= prev.style then
                total = total + 1
            end
            if node.key ~= nil then
                total = total + 1
            end

            -- enum-style __eq comparison (event.keyCode == Key.escape)
            local keyCode = Key[(i % 5 == 0) and "escape" or "left"]
            if keyCode == Key.escape then
                total = total + 1
            end
            if keyCode ~= Key.enter then
                total = total + 1
            end
        end
    end
    return total
end

-- Executes the workload and returns the total so the chunk body actually runs
-- when the benchmark calls the chunk (state.CallAsync(closure, [])).
return run()
