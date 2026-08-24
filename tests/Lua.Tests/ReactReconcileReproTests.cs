using System.Text;
using Lua.IO;
using Lua.Platforms;
using Lua.Standard;
using Lua.Tests.Helpers;

namespace Lua.Tests;

/// <summary>
/// Diagnostic repro for "components multiply on re-render" in the Lua React
/// clone. Loads the real react.luau + styled.luau from the repo and drives a
/// list component through several state updates while counting host nodes.
/// </summary>
public sealed class ReactReconcileReproTests
{
    sealed class MemoryFileSystem(Dictionary<string, string> files)
        : NotImplementedExceptionFileSystemBase
    {
        public override bool IsReadable(string path) => files.ContainsKey(path);

        public override bool DirectoryExists(string path) => false;

        public override string DirectorySeparator => "/";

        public override ValueTask<ILuaStream> Open(
            string path,
            LuaFileOpenMode mode,
            CancellationToken cancellationToken
        )
        {
            if (!files.TryGetValue(path, out var content))
            {
                throw new FileNotFoundException($"File '{path}' not found");
            }

            return new(ILuaStream.CreateFromMemory(content.AsMemory()));
        }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "NFMWorld.Library", "data", "library")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root not found");
    }

    [Test]
    public void Reproduce_NodeMultiplication_OnStateUpdate()
    {
        var root = FindRepoRoot();
        var lib = Path.Combine(root, "NFMWorld.Library", "data", "library");

        var mainScript = """
            local React = require('./react')

            local host = {
              instances = 0, appends = 0, removals = 0,
            }

            function host.createInstance(vtype, props)
              host.instances = host.instances + 1
              return { type = vtype, props = props, children = {} }
            end

            function host.createTextInstance(text)
              host.instances = host.instances + 1
              return { type = 'text', text = text, children = {} }
            end

            function host.appendChild(parent, child)
              host.appends = host.appends + 1
              for _, c in ipairs(parent.children) do
                if c == child then error('DUPLICATE APPEND ' .. tostring(child.type)) end
              end
              parent.children[#parent.children + 1] = child
            end

            function host.insertBefore(parent, child, before)
              host.appends = host.appends + 1
              for _, c in ipairs(parent.children) do
                if c == child then error('DUPLICATE INSERT ' .. tostring(child.type)) end
              end
              local idx = 1
              for i, c in ipairs(parent.children) do
                if c == before then idx = i break end
              end
              table.insert(parent.children, idx, child)
            end

            function host.removeChild(parent, child)
              host.removals = host.removals + 1
              for i, c in ipairs(parent.children) do
                if c == child then table.remove(parent.children, i) return end
              end
              error('REMOVE MISS ' .. tostring(child.type))
            end

            function host.setProperty() end
            function host.commitTextUpdate() end

            React.setHostConfig(host)

            local root = { type = 'root', children = {} }
            local setCountGlobal = nil

            local Item = function(props)
              return React.h('view', { key = props.key }, props.name)
            end

            local function App(props)
              local count, setCount = React.useState(2)
              setCountGlobal = setCount
              local items = {}
              for i = 1, count do
                items[i] = React.h(Item, { key = 'item' .. i, name = 'n' .. i })
              end
              return React.h('view', nil, items)
            end

            local function countNodes(node)
              local n = 1
              for _, c in ipairs(node.children) do
                n = n + countNodes(c)
              end
              return n
            end

            React.render(React.h(App), root)
            local before = countNodes(root)
            for _ = 1, 5 do
              setCountGlobal(2)
              setCountGlobal(3)
              setCountGlobal(2)
            end
            local after = countNodes(root)
            return before, after, host.instances, host.appends, host.removals
            """;

        var files = new Dictionary<string, string>
        {
            ["main.lua"] = mainScript,
            ["react.lua"] = File.ReadAllText(Path.Combine(lib, "react.luau")),
            ["styled.lua"] = File.ReadAllText(Path.Combine(lib, "styled.luau")),
            ["reactlib/types.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "types.luau")),
            ["reactlib/vdom.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "vdom.luau")),
            ["reactlib/core.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "core.luau")),
        };

        var fs = new MemoryFileSystem(files);
        var state = LuaState.Create(
            new LuaPlatform(
                FileSystem: fs,
                OsEnvironment: null!,
                StandardIO: new ConsoleStandardIO(),
                TimeProvider: TimeProvider.System
            )
            {
                RequireByString = true
            }
        );
        state.OpenStandardLibraries();
        state.OpenStringLibrary();
        state.OpenTableLibrary();

        var result = state.DoFile("main.lua");

        var before = result[0].Read<double>();
        var after = result[1].Read<double>();
        var instances = result[2].Read<double>();
        var appends = result[3].Read<double>();
        var removals = result[4].Read<double>();

        TestContext.Progress.WriteLine($"instances={instances} appends={appends} removals={removals} nodes={before}->{after}");
        Assert.That(after, Is.EqualTo(before), "host tree grew across re-renders");
    }

    /// <summary>
    /// Regression test for the host-fiber bailout: a subtree whose VNode is
    /// cached with useMemo (reference-identical props table) must NOT be
    /// re-walked / re-rendered when an unrelated state change triggers a
    /// re-render. Its child components' render count must stay flat.
    /// </summary>
    [Test]
    public void Memoized_Subtree_SkipsRerender_OnUnrelatedStateChange()
    {
        var root = FindRepoRoot();
        var lib = Path.Combine(root, "NFMWorld.Library", "data", "library");

        var mainScript = """
            local React = require('./react')

            local host = {
              instances = 0, appends = 0, removals = 0,
            }

            function host.createInstance(vtype, props)
              host.instances = host.instances + 1
              return { type = vtype, props = props, children = {} }
            end

            function host.createTextInstance(text)
              host.instances = host.instances + 1
              return { type = 'text', text = text, children = {} }
            end

            function host.appendChild(parent, child)
              host.appends = host.appends + 1
              for _, c in ipairs(parent.children) do
                if c == child then error('DUPLICATE APPEND') end
              end
              parent.children[#parent.children + 1] = child
            end

            function host.insertBefore(parent, child, before)
              host.appends = host.appends + 1
              local idx = 1
              for i, c in ipairs(parent.children) do
                if c == before then idx = i break end
              end
              table.insert(parent.children, idx, child)
            end

            function host.removeChild(parent, child)
              host.removals = host.removals + 1
              for i, c in ipairs(parent.children) do
                if c == child then table.remove(parent.children, i) return end
              end
              error('REMOVE MISS')
            end

            function host.setProperty() end
            function host.commitTextUpdate() end

            React.setHostConfig(host)

            local root = { type = 'root', children = {} }
            local setOtherGlobal = nil

            -- Counts how many times the memoized subtree's rows actually render.
            local renderCount = 0
            local Row = function(props)
              renderCount = renderCount + 1
              return React.h('view', nil, tostring(props.i))
            end

            local function App(props)
              local count, setCount = React.useState(0)
              local other, setOther = React.useState(0)
              setOtherGlobal = setOther
              -- Cached once: the SAME VNode (and props table) every render.
              local memoized = React.useMemo(function()
                local children = {}
                for i = 1, 50 do
                  children[i] = React.h(Row, { key = 'row' .. i, i = i })
                end
                return React.h('view', { key = 'list' }, children)
              end, {})
              return React.h('view', nil, memoized,
                React.h('text', nil, 'c=' .. tostring(count) .. 'o=' .. tostring(other)))
            end

            local function countNodes(node)
              local n = 1
              for _, c in ipairs(node.children) do
                n = n + countNodes(c)
              end
              return n
            end

            React.render(React.h(App), root)
            local rendersAfterMount = renderCount
            local nodesAfterMount = countNodes(root)
            local instancesAfterMount = host.instances

            -- Unrelated state changes: the memoized subtree must NOT re-render.
            for _ = 1, 3 do
              setOtherGlobal(1)
              setOtherGlobal(2)
              setOtherGlobal(1)
            end

            return rendersAfterMount, renderCount, nodesAfterMount, countNodes(root), instancesAfterMount, host.instances
            """;

        var files = new Dictionary<string, string>
        {
            ["main.lua"] = mainScript,
            ["react.lua"] = File.ReadAllText(Path.Combine(lib, "react.luau")),
            ["styled.lua"] = File.ReadAllText(Path.Combine(lib, "styled.luau")),
            ["reactlib/types.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "types.luau")),
            ["reactlib/vdom.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "vdom.luau")),
            ["reactlib/core.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "core.luau")),
        };

        var fs = new MemoryFileSystem(files);
        var state = LuaState.Create(
            new LuaPlatform(
                FileSystem: fs,
                OsEnvironment: null!,
                StandardIO: new ConsoleStandardIO(),
                TimeProvider: TimeProvider.System
            )
            {
                RequireByString = true
            }
        );
        state.OpenStandardLibraries();
        state.OpenStringLibrary();
        state.OpenTableLibrary();

        var result = state.DoFile("main.lua");

        var rendersAfterMount = result[0].Read<double>();
        var rendersAfterUnrelated = result[1].Read<double>();
        var nodesAfterMount = result[2].Read<double>();
        var nodesAfter = result[3].Read<double>();
        var instancesAfterMount = result[4].Read<double>();
        var instancesAfter = result[5].Read<double>();

        TestContext.Progress.WriteLine(
            $"renders mount={rendersAfterMount} unrelated={rendersAfterUnrelated} nodes={nodesAfterMount}->{nodesAfter} instances={instancesAfterMount}->{instancesAfter}");

        Assert.That(
            rendersAfterUnrelated,
            Is.EqualTo(rendersAfterMount),
            "memoized subtree was re-rendered on an unrelated state change (bailout not engaged)");
        Assert.That(nodesAfter, Is.EqualTo(nodesAfterMount), "host tree grew across unrelated re-renders");
        Assert.That(instancesAfter, Is.EqualTo(instancesAfterMount), "host instances grew across unrelated re-renders");
    }

    static LuaValue[] RunReactScript(string mainScript)
    {
        var root = FindRepoRoot();
        var lib = Path.Combine(root, "NFMWorld.Library", "data", "library");

        var files = new Dictionary<string, string>
        {
            ["main.lua"] = mainScript,
            ["react.lua"] = File.ReadAllText(Path.Combine(lib, "react.luau")),
            ["styled.lua"] = File.ReadAllText(Path.Combine(lib, "styled.luau")),
            ["reactlib/types.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "types.luau")),
            ["reactlib/vdom.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "vdom.luau")),
            ["reactlib/core.lua"] = File.ReadAllText(Path.Combine(lib, "reactlib", "core.luau")),
        };

        var fs = new MemoryFileSystem(files);
        var state = LuaState.Create(
            new LuaPlatform(
                FileSystem: fs,
                OsEnvironment: null!,
                StandardIO: new ConsoleStandardIO(),
                TimeProvider: TimeProvider.System
            )
            {
                RequireByString = true
            }
        );
        state.OpenStandardLibraries();
        state.OpenStringLibrary();
        state.OpenTableLibrary();

        return state.DoFile("main.lua");
    }

    /// <summary>
    /// React.memo skips re-rendering when props are shallow-equal (even with a
    /// fresh props table), and re-renders when a prop value actually changes.
    /// </summary>
    [Test]
    public void Memo_Component_SkipsRerender_WhenPropsShallowEqual()
    {
        var result = RunReactScript("""
            local React = require('./react')

            local host = { instances = 0 }
            function host.createInstance(vtype, props) host.instances = host.instances + 1; return { type = vtype, props = props, children = {} } end
            function host.createTextInstance(text) host.instances = host.instances + 1; return { type = 'text', text = text, children = {} } end
            function host.appendChild(p, c) p.children[#p.children + 1] = c end
            function host.insertBefore(p, c, b) local i = 1; for j, x in ipairs(p.children) do if x == b then i = j break end end; table.insert(p.children, i, c) end
            function host.removeChild(p, c) for i, x in ipairs(p.children) do if x == c then table.remove(p.children, i) return end end error('REMOVE') end
            function host.setProperty() end
            function host.commitTextUpdate() end
            React.setHostConfig(host)

            local root = { type = 'root', children = {} }
            local renderCount = 0

            local Child = function(props)
              renderCount = renderCount + 1
              return React.h('text', nil, props.label .. tostring(props.n))
            end
            local MemoChild = React.memo(Child)

            local setCountGlobal = nil
            local setOtherGlobal = nil
            local function App(props)
              local count, setCount = React.useState(0)
              local other, setOther = React.useState(0)
              setCountGlobal = setCount
              setOtherGlobal = setOther
              return React.h('view', nil, React.h(MemoChild, { label = 'x', n = count }))
            end

            React.render(React.h(App), root)
            local afterMount = renderCount
            setOtherGlobal(1) -- unrelated: memo skips (props shallow-equal)
            local afterUnrelated = renderCount
            setCountGlobal(1) -- n 0->1: memo re-renders
            local afterRealChange = renderCount
            return afterMount, afterUnrelated, afterRealChange
            """);

        var afterMount = result[0].Read<double>();
        var afterUnrelated = result[1].Read<double>();
        var afterRealChange = result[2].Read<double>();

        TestContext.Progress.WriteLine($"render count mount={afterMount} unrelated={afterUnrelated} realChange={afterRealChange}");
        Assert.That(afterUnrelated, Is.EqualTo(afterMount), "memo component re-rendered on shallow-equal props");
        Assert.That(afterRealChange, Is.EqualTo(afterMount + 1), "memo component failed to re-render on a real prop change");
    }

    /// <summary>
    /// A React.memo component that reads context must re-render when the
    /// context value changes, even though its props are shallow-equal.
    /// </summary>
    [Test]
    public void Memo_Component_Rerenders_WhenContextChanges()
    {
        var result = RunReactScript("""
            local React = require('./react')

            local host = { instances = 0 }
            function host.createInstance(vtype, props) host.instances = host.instances + 1; return { type = vtype, props = props, children = {} } end
            function host.createTextInstance(text) host.instances = host.instances + 1; return { type = 'text', text = text, children = {} } end
            function host.appendChild(p, c) p.children[#p.children + 1] = c end
            function host.insertBefore(p, c, b) local i = 1; for j, x in ipairs(p.children) do if x == b then i = j break end end; table.insert(p.children, i, c) end
            function host.removeChild(p, c) for i, x in ipairs(p.children) do if x == c then table.remove(p.children, i) return end end error('REMOVE') end
            function host.setProperty() end
            function host.commitTextUpdate() end
            React.setHostConfig(host)

            local root = { type = 'root', children = {} }
            local renderCount = 0

            local Ctx = React.createContext('default')
            local Consumer = function(props)
              local value = React.useContext(Ctx)
              renderCount = renderCount + 1
              return React.h('text', nil, value)
            end
            local MemoConsumer = React.memo(Consumer)

            local setValGlobal = nil
            local function App(props)
              local val, setVal = React.useState('a')
              setValGlobal = setVal
              return React.h(Ctx.Provider, { value = val }, React.h(MemoConsumer, { label = 'x' }))
            end

            React.render(React.h(App), root)
            local afterMount = renderCount
            setValGlobal('b') -- context value changed; memo must NOT skip
            local afterContextChange = renderCount
            return afterMount, afterContextChange
            """);

        var afterMount = result[0].Read<double>();
        var afterContextChange = result[1].Read<double>();

        TestContext.Progress.WriteLine($"render count mount={afterMount} contextChange={afterContextChange}");
        Assert.That(afterContextChange, Is.EqualTo(afterMount + 1), "memo context consumer did not re-render on context change");
    }

    /// <summary>
    /// The host-fiber bailout must be invalidated when a stateful component
    /// lives inside the bailed subtree — otherwise its setState would be
    /// silently dropped.
    /// </summary>
    [Test]
    public void Host_Bailout_Invalidated_By_Stateful_Descendant()
    {
        var result = RunReactScript("""
            local React = require('./react')

            local host = { instances = 0 }
            function host.createInstance(vtype, props) host.instances = host.instances + 1; return { type = vtype, props = props, children = {} } end
            function host.createTextInstance(text) host.instances = host.instances + 1; return { type = 'text', text = text, children = {} } end
            function host.appendChild(p, c) p.children[#p.children + 1] = c end
            function host.insertBefore(p, c, b) local i = 1; for j, x in ipairs(p.children) do if x == b then i = j break end end; table.insert(p.children, i, c) end
            function host.removeChild(p, c) for i, x in ipairs(p.children) do if x == c then table.remove(p.children, i) return end end error('REMOVE') end
            function host.setProperty() end
            function host.commitTextUpdate() end
            React.setHostConfig(host)

            local root = { type = 'root', children = {} }
            local renderCount = 0
            local holder = { f = nil }

            local Stateful = function(props)
              local v, setV = React.useState(0)
              holder.f = setV
              renderCount = renderCount + 1
              return React.h('text', nil, 'v=' .. tostring(v))
            end

            local function App(props)
              local count, setCount = React.useState(0)
              -- memoize a HOST subtree that CONTAINS a stateful child
              local memoized = React.useMemo(function()
                return React.h('view', { key = 'wrap' }, React.h(Stateful, { tag = 's' }))
              end, {})
              return React.h('view', nil, memoized, React.h('text', nil, 'c=' .. tostring(count)))
            end

            React.render(React.h(App), root)
            local afterMount = renderCount
            holder.f(5) -- stateful descendant updates; host bailout must not drop it
            local afterState = renderCount
            return afterMount, afterState
            """);

        var afterMount = result[0].Read<double>();
        var afterState = result[1].Read<double>();

        TestContext.Progress.WriteLine($"render count mount={afterMount} afterState={afterState}");
        Assert.That(afterState, Is.EqualTo(afterMount + 1), "stateful descendant's update was dropped by a host bailout");
    }
}
