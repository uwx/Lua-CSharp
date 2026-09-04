using Lua.IO;
using Lua.Platforms;
using Lua.Standard;
using Lua.Tests.Helpers;

namespace Lua.Tests;

/// <summary>
/// Tests for the Sx fine-grained reactive UI framework
/// (NFMWorld.Library/data/library/sx/*.luau).
///
/// Loads the REAL .luau modules through a memory FS (like ReactReconcileReproTests),
/// injects a fake UiLib host, and asserts the fine-grained behaviour that motivated the
/// framework: a single signal write must produce ONE host commitTextUpdate/setProperty,
/// never a full-tree rebuild.
/// </summary>
public sealed class SxReactiveTests
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

    static LuaValue[] Run(string mainScript)
    {
        var root = FindRepoRoot();
        var lib = Path.Combine(root, "NFMWorld.Library", "data", "library");

        var files = new Dictionary<string, string>
        {
            ["main.lua"] = mainScript,
            ["sx/signals.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "signals.luau")),
            ["sx/host.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "host.luau")),
            ["sx/h.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "h.luau")),
            ["sx/types.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "types.luau")),
            ["sx/dom.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "dom.luau")),
            ["sx/styled.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "styled.luau")),
            ["sx/index.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "index.luau")),
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
        var values = new LuaValue[result.Length];
        for (var i = 0; i < result.Length; i++)
        {
            values[i] = result[i];
        }

        return values;
    }

    /// <summary>
    /// Injected Lua-side fake host, exposed as `_G.UiLib` (host.luau captures it at load).
    /// `defer` runs synchronously so effects flush inline (no end-of-frame scheduler in
    /// these unit tests — Sx.setScheduler is never called, so writes flush synchronously).
    /// </summary>
    const string FakeUiLib = """
        _G.UiLib = {
          instances = 0, appends = 0, removals = 0, setProps = 0, commits = 0,
          activeRoot = nil,
          createRoot = function()
            return { type = 'root', children = {} }
          end,
          createInstance = function(vtype, props)
            _G.UiLib.instances = _G.UiLib.instances + 1
            return { type = vtype, props = props, children = {} }
          end,
          createTextInstance = function(text)
            _G.UiLib.instances = _G.UiLib.instances + 1
            return { type = 'text', text = text, children = {} }
          end,
          appendChild = function(parent, child)
            _G.UiLib.appends = _G.UiLib.appends + 1
            parent.children[#parent.children + 1] = child
          end,
          insertBefore = function(parent, child, before)
            _G.UiLib.appends = _G.UiLib.appends + 1
            local idx = nil
            for i, c in ipairs(parent.children) do
              if c == before then idx = i break end
            end
            if idx == nil then error('insertBefore: before node is not a child of parent') end
            table.insert(parent.children, idx, child)
          end,
          removeChild = function(parent, child)
            _G.UiLib.removals = _G.UiLib.removals + 1
            for i, c in ipairs(parent.children) do
              if c == child then table.remove(parent.children, i) return end
            end
          end,
          setProperty = function(inst, key, value)
            _G.UiLib.setProps = _G.UiLib.setProps + 1
          end,
          commitTextUpdate = function(textInst, oldText, newText)
            _G.UiLib.commits = _G.UiLib.commits + 1
            textInst.text = newText
          end,
          setActiveRoot = function(root)
            _G.UiLib.activeRoot = root
          end,
          defer = function(fn) fn() end,
        }

        local Sx = require('./sx/index')
        local x = Sx.x

        -- collect visible (non-empty) text node contents in tree order
        local function collectTexts(node)
          local out = {}
          local function walk(n)
            if n.type == 'text' and n.text ~= '' then
              out[#out + 1] = n.text
            end
            for _, c in ipairs(n.children) do
              walk(c)
            end
          end
          walk(node)
          return out
        end
        """ + "\n";

    [Test]
    public void Signal_Effect_RunsOnChange_NotOnUnrelatedWrite()
    {
        var main = FakeUiLib + """
            local a, setA = Sx.createSignal(0)
            local b, setB = Sx.createSignal(0)
            local runs = 0
            Sx.createEffect(function()
              runs = runs + 1
              local _ = a()
            end)
            setB(1)
            local afterUnrelated = runs
            setA(1)
            local afterChange = runs
            return afterUnrelated, afterChange
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1), "effect must not re-run on an unrelated signal write");
        Assert.That(result[1].Read<double>(), Is.EqualTo(2), "effect must re-run when its dependency changes");
    }

    [Test]
    public void Memo_LazyRecompute_And_PropagatesToEffect()
    {
        var main = FakeUiLib + """
            local s, setS = Sx.createSignal(1)
            local memoRuns = 0
            local m = Sx.createMemo(function()
              memoRuns = memoRuns + 1
              return s() * 10
            end)
            -- memo is lazy: not computed until first read
            local beforeFirstRead = memoRuns
            local effectRuns = 0
            Sx.createEffect(function()
              effectRuns = effectRuns + 1
              local _ = m()
            end)
            local afterMount = memoRuns
            local valAtMount = m()
            local afterSecondRead = memoRuns
            local valAtSecondRead = m()
            -- change source: memo recomputes (pull) and effect re-runs
            setS(2)
            local effectAfterChange = effectRuns
            local memoAfterChange = memoRuns
            local valAfterChange = m()
            return beforeFirstRead, afterMount, afterSecondRead, effectAfterChange, memoAfterChange, valAtMount, valAtSecondRead, valAfterChange
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(0), "memo must be lazy");
        Assert.That(result[1].Read<double>(), Is.EqualTo(1), "first read computes the memo");
        Assert.That(result[2].Read<double>(), Is.EqualTo(1), "cached memo must not recompute on repeat reads");
        Assert.That(result[3].Read<double>(), Is.EqualTo(2), "effect must re-run after source change");
        Assert.That(result[4].Read<double>(), Is.EqualTo(2), "memo must recompute once when its source changes");
        Assert.That(result[5].Read<double>(), Is.EqualTo(10), "memo value must be 10 at mount");
        Assert.That(result[6].Read<double>(), Is.EqualTo(10), "memo value must stay cached at 10");
        Assert.That(result[7].Read<double>(), Is.EqualTo(20), "memo value must be 20 after source change");
    }

    [Test]
    public void Batch_CoalescesMultipleWrites_IntoOneEffectPass()
    {
        var main = FakeUiLib + """
            local s, setS = Sx.createSignal(0)
            local runs = 0
            local seen = nil
            Sx.createEffect(function()
              runs = runs + 1
              seen = s()
            end)
            Sx.batch(function()
              setS(1)
              setS(2)
              setS(3)
            end)
            return runs, seen
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(2), "initial run + one batched run (not 4)");
        Assert.That(result[1].Read<double>(), Is.EqualTo(3), "effect must see the final value");
    }

    [Test]
    public void Root_Disposal_RunsCleanups_And_StopsEffects()
    {
        var main = FakeUiLib + """
            local s, setS = Sx.createSignal(0)
            local effectRuns = 0
            local cleanupRan = false
            local dispose = nil
            Sx.createRoot(function(d)
              dispose = d
              Sx.createEffect(function()
                effectRuns = effectRuns + 1
                local _ = s()
              end)
              Sx.onCleanup(function()
                cleanupRan = true
              end)
            end)
            setS(1)
            dispose()
            local runsBeforeDispose = effectRuns
            setS(2)
            local runsAfterDispose = effectRuns
            return runsBeforeDispose, runsAfterDispose, cleanupRan and 1 or 0
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(2), "effect ran at mount + after write");
        Assert.That(result[1].Read<double>(), Is.EqualTo(2), "disposed effect must not run again");
        Assert.That(result[2].Read<double>(), Is.EqualTo(1), "onCleanup must run on dispose");
    }

    [Test]
    public void Devtools_TreeSnapshot_ListsMountedComponentsAndHosts()
    {
        var main = FakeUiLib + """
            Sx.devtools.enable()
            local function Card() return x('view') { x('text') { 'hi' } } end
            local Card2 = function() return x('view') { x('text') { 'hi2' } } end
            Sx.render(x('view') {
              x(Card) {},
              x(Card2) {},
            })
            local tree = Sx.devtools.snapshot()
            local names = {}
            local function walk(n)
              names[#names + 1] = n.name .. ':' .. n.kind
              for _, c in ipairs(n.children) do walk(c) end
            end
            walk(tree)
            return table.concat(names, ',')
            """;

        var result = Run(main);
        var flat = result[0].Read<string>();
        TestContext.Progress.WriteLine($"tree = {flat}");
        Assert.That(flat, Does.Contain("view:host"), "root host element is captured");
        // Lua-CSharp records the function's declaration name on its prototype, so both the
        // `local function X()` and `local X = function()` forms resolve to real names, now
        // with the definition-site call site appended (e.g. "Card (main.lua:65)").
        Assert.That(flat, Does.Contain("Card (main.lua:"), "`local function X()` component resolves to its name + call site");
        Assert.That(flat, Does.Contain("Card2 (main.lua:"), "`local X = function()` component resolves to its variable name + call site");
        Assert.That(flat, Does.Contain(":component"), "component nodes are still tagged with their kind");
        Assert.That(flat, Does.Contain("text:host"), "leaf host element is captured");
    }

    [Test]
    public void Devtools_DebugList_ReturnsLiveSignalAndMemoNodes()
    {
        var main = FakeUiLib + """
            Sx.createSignal(1)
            Sx.createMemo(function() return 2 end)
            local entries = Sx.debug.list()
            local sigs, comps = 0, 0
            for _, e in ipairs(entries) do
              if e.kind == 'signal' then sigs = sigs + 1 end
              if e.kind == 'computation' then comps = comps + 1 end
            end
            return sigs, comps
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1), "created signal is registered");
        Assert.That(result[1].Read<double>(), Is.EqualTo(1), "created memo (computation) is registered");
    }

    [Test]
    public void Devtools_Snapshot_CarriesHostRef()
    {
        // The hover-highlight feature needs each host node in the devtools tree snapshot to
        // carry a live `host` reference so the devtools can position an overlay over it.
        var main = FakeUiLib + """
            Sx.devtools.enable()
            local function Card()
              return x('view') { style = { backgroundColor = '#ff0000' }, x('text') { 'hi' } }
            end
            Sx.render(x('view') { x(Card) {} })
            local tree = Sx.devtools.snapshot()
            local foundHost = false
            local function walk(n)
              if n.host ~= nil then foundHost = true end
              for _, c in ipairs(n.children or {}) do walk(c) end
            end
            walk(tree)
            return foundHost and 1 or 0
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1), "snapshot carries a live host ref for hover highlighting");
    }

    [Test]
    public void Devtools_Snapshot_CarriesProps()
    {
        // The devtools props pane reads the hovered node's props off the snapshot. Both
        // host nodes (their style/event props) and component nodes (their props incl.
        // `children`) must carry a `props` field.
        var main = FakeUiLib + """
            Sx.devtools.enable()
            local function Card()
              return x('view') { style = { backgroundColor = '#ff0000' }, onmousedown = function() end }
            end
            Sx.render(x('view') { x(Card) {} })
            local tree = Sx.devtools.snapshot()
            local foundHostStyle, foundCompChildren = false, false
            local function walk(n)
              if n.props ~= nil then
                if n.kind == 'host' and n.props.style ~= nil then foundHostStyle = true end
                if n.kind == 'component' and n.props.children ~= nil then foundCompChildren = true end
              end
              for _, c in ipairs(n.children or {}) do walk(c) end
            end
            walk(tree)
            return (foundHostStyle and 1 or 0), (foundCompChildren and 1 or 0)
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1), "host node carries its props (style)");
        Assert.That(result[1].Read<double>(), Is.EqualTo(1), "component node carries its props (children)");
    }

    [Test]
    public void Devtools_OwnerName_AttributesSignalToOwningComponent()
    {
        var main = FakeUiLib + """
            Sx.devtools.enable()
            local function Card()
              local count, setCount = Sx.createSignal(0)
              return x('view') { x('text') { function() return tostring(count()) end } }
            end
            Sx.render(x('view') { x(Card) {} })
            local ownedByCard = false
            for _, e in ipairs(Sx.debug.list()) do
              if e.kind == 'signal' and string.find(Sx.debug.ownerName(e.node) or '', 'Card', 1, true) ~= nil then
                ownedByCard = true
              end
            end
            return ownedByCard and 1 or 0
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1), "a signal created inside a component is attributed to that component");
    }

    [Test]
    public void Devtools_ComponentName_StyledComponentsShowTagName()
    {
        // Styled components are anonymous closures, so debug.info can't name them. They
        // should still show a readable "Styled<tag>" name in the devtools tree via the
        // weak-keyed registry (styled -> Sx.debug.setStyledName).
        var main = FakeUiLib + """
            Sx.devtools.enable()
            local Card = Sx.styled('view') {
              backgroundColor = '#ff0000',
            }
            local Label = Sx.styled('text') {
              color = '#ffffff',
            }
            Sx.render(x('view') {
              x(Card) { style = { padding = 4 } },
              x(Label) { 'hello' },
            })
            local tree = Sx.devtools.snapshot()
            local styledTotal, styledWithSite = 0, 0
            local function walk(n)
              if n.kind == 'component' then
                -- Styled closures are anonymous; they must still resolve to a real name
                -- (their inner closure name + definition-site call site), never "anonymous".
                styledTotal = styledTotal + 1
                if n.name ~= 'anonymous' and string.find(n.name, '(main.lua:', 1, true) ~= nil then
                  styledWithSite = styledWithSite + 1
                end
              end
              for _, c in ipairs(n.children or {}) do walk(c) end
            end
            walk(tree)
            return (styledWithSite >= 2 and 1 or 0), (styledTotal >= 2 and 1 or 0)
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1), "both styled components carry a call site, not 'anonymous'");
        Assert.That(result[1].Read<double>(), Is.EqualTo(1), "both styled components resolve to a real name + call site");
    }

    [Test]
    public void Show_MountsChildren_And_SwapsToFallback()
    {
        var main = FakeUiLib + """
            local show, setShow = Sx.createSignal(true)
            Sx.render(x(Sx.Show) {
              when = show,
              fallback = x('view') { 'hidden' },
              x('view') { 'shown' },
            })
            local root = _G.UiLib.activeRoot
            local shownTexts = table.concat(collectTexts(root), ',')
            setShow(false)
            local hiddenTexts = table.concat(collectTexts(root), ',')
            return shownTexts, hiddenTexts
            """;

        var result = Run(main);
        Assert.That(result[0].Read<string>(), Is.EqualTo("shown"));
        Assert.That(result[1].Read<string>(), Is.EqualTo("hidden"));
    }

    [Test]
    public void Switch_PicksFirstMatchingMatch()
    {
        var main = FakeUiLib + """
            local a, setA = Sx.createSignal(true)
            local b, setB = Sx.createSignal(false)
            Sx.render(x(Sx.Switch) {
              x(Sx.Match) { when = a, x('view') { 'A' } },
              x(Sx.Match) { when = b, x('view') { 'B' } },
            })
            local root = _G.UiLib.activeRoot
            local first = table.concat(collectTexts(root), ',')
            setA(false)
            setB(true)
            local second = table.concat(collectTexts(root), ',')
            return first, second
            """;

        var result = Run(main);
        Assert.That(result[0].Read<string>(), Is.EqualTo("A"));
        Assert.That(result[1].Read<string>(), Is.EqualTo("B"));
    }

    [Test]
    public void Switch_WhenSignalChanges_ButChosenMatchSame_DoesNotRemount()
    {
        // Regression: a `Match.when` that reads a config signal (e.g. the settings
        // loading-state `when`) re-runs on every config write. The Switch must NOT
        // remount the chosen subtree in that case — only a change of WHICH match is
        // selected may cause structural work. Mirrors the settings.luau structure.
        var main = FakeUiLib + """
            local config, setConfig = Sx.createSignal(nil)
            local options, setOptions = Sx.createSignal(nil)
            Sx.render(x(Sx.Switch) {
              x(Sx.Match) {
                when = function() return config() == nil or options() == nil end,
                x('view') { 'loading' },
              },
              x(Sx.Match) {
                when = function() return true end,
                x('view') {
                  function() return config() ~= nil and config().fps or 'n/a' end,
                },
              },
            })
            local root = _G.UiLib.activeRoot

            -- load settings: transitions loading -> loaded (a real, expected remount)
            setOptions({})
            setConfig({ fps = 60 })
            local loadedTexts = table.concat(collectTexts(root), ',')

            -- change a setting: only the reactive text child should update, no remount
            local appendsBefore = _G.UiLib.appends
            local removalsBefore = _G.UiLib.removals
            setConfig({ fps = 120 })
            local appends = _G.UiLib.appends - appendsBefore
            local removals = _G.UiLib.removals - removalsBefore
            local afterTexts = table.concat(collectTexts(root), ',')
            return loadedTexts, appends, removals, afterTexts
            """;

        var result = Run(main);
        Assert.That(result[0].Read<string>(), Is.EqualTo("60"), "loaded branch shows the config value");
        Assert.That(result[1].Read<double>(), Is.EqualTo(0), "no append/insert when the chosen match stays the same");
        Assert.That(result[2].Read<double>(), Is.EqualTo(0), "no removal when the chosen match stays the same");
        Assert.That(result[3].Read<string>(), Is.EqualTo("120"), "reactive child still updates in place");
    }

    [Test]
    public void For_Keyed_SingleRowUpdate_TouchesOnlyThatRow()
    {
        var main = FakeUiLib + """
            -- each item carries its own label signal; the row text reads it reactively.
            local makeItem = function(id, label)
              local l, setL = Sx.createSignal(label)
              return { id = id, label = l, setLabel = setL }
            end
            local items, setItems = Sx.createSignal({
              makeItem(1, 'a'),
              makeItem(2, 'b'),
            })

            Sx.render(x('view') {
              x(Sx.For) {
                each = items,
                function(item, index)
                  return x('text') { function()
                    return item().label()
                  end }
                end,
              },
            })
            local root = _G.UiLib.activeRoot

            -- update ONE row's label signal
            local commitsBefore = _G.UiLib.commits
            local appendsBefore = _G.UiLib.appends
            local removalsBefore = _G.UiLib.removals
            items()[1].setLabel('a2')
            local commits = _G.UiLib.commits - commitsBefore
            local appends = _G.UiLib.appends - appendsBefore
            local removals = _G.UiLib.removals - removalsBefore
            local texts = table.concat(collectTexts(root), ',')
            return commits, appends, removals, texts
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1), "exactly one commitTextUpdate for the updated row");
        Assert.That(result[1].Read<double>(), Is.EqualTo(0), "no structural insert/append on a row update");
        Assert.That(result[2].Read<double>(), Is.EqualTo(0), "no structural removal on a row update");
        Assert.That(result[3].Read<string>(), Is.EqualTo("a2,b"), "row texts must reflect the new label");
    }

    [Test]
    public void For_Keyed_AddRemove_RebuildsRows()
    {
        var main = FakeUiLib + """
            local makeItem = function(id, label)
              return { id = id, label = label }
            end
            local items, setItems = Sx.createSignal({
              makeItem(1, 'a'),
              makeItem(2, 'b'),
            })
            Sx.render(x('view') {
              x(Sx.For) {
                each = items,
                function(item, index)
                  return x('text') { item().label }
                end,
              },
            })
            local root = _G.UiLib.activeRoot
            local before = #collectTexts(root)
            setItems({
              makeItem(1, 'a'),
              makeItem(2, 'b'),
              makeItem(3, 'c'),
            })
            local after = #collectTexts(root)
            return before, after
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(2), "two rows initially");
        Assert.That(result[1].Read<double>(), Is.EqualTo(3), "three rows after adding an item");
    }

    [Test]
    public void SingleLeafHudUpdate_IsOneCommit_NoFullTreeRebuild()
    {
        var main = FakeUiLib + """
            -- The headline scenario: a HUD speed readout as a dynamic text child.
            local speed, setSpeed = Sx.createSignal(50)
            Sx.render(x('view') {
              x('text') { function()
                return tostring(math.floor(speed() + 0.5))
              end },
              x('text') { 'KM/H' },
            })

            -- Warm mount effects are done; now measure a per-frame speed change.
            local c0, a0, r0, s0 = _G.UiLib.commits, _G.UiLib.appends, _G.UiLib.removals, _G.UiLib.setProps
            setSpeed(96)
            local commits = _G.UiLib.commits - c0
            local appends = _G.UiLib.appends - a0
            local removals = _G.UiLib.removals - r0
            local setProps = _G.UiLib.setProps - s0
            return commits, appends, removals, setProps
            """;

        var result = Run(main);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1), "one commitTextUpdate for the speed leaf");
        Assert.That(result[1].Read<double>(), Is.EqualTo(0), "no append/insert on a leaf update");
        Assert.That(result[2].Read<double>(), Is.EqualTo(0), "no removal on a leaf update");
        Assert.That(result[3].Read<double>(), Is.EqualTo(0), "no setProperty on a leaf update");
    }

    // ---------------------------------------------------------------- router/mainmenu port
    /// <summary>
    /// Loads the REAL data/uis/router.luau + data/uis/routes/mainmenu.luau (Sx ports)
    /// plus the sx module graph, drives account + navigation events, and asserts the
    /// menu renders, reacts to account, and navigates pages (PLAY -> BACK).
    /// </summary>
    static LuaValue[] RunPort(string mainScript)
    {
        var root = FindRepoRoot();
        var lib = Path.Combine(root, "NFMWorld.Library", "data", "library");
        var uis = Path.Combine(root, "NFMWorld.Library", "data", "uis");

        var files = new Dictionary<string, string>
        {
            ["main.lua"] = mainScript,
            ["uis/router.lua"] = File.ReadAllText(Path.Combine(uis, "router.luau")),
            ["uis/routes/mainmenu.lua"] = File.ReadAllText(Path.Combine(uis, "routes", "mainmenu.luau")),
            ["uis/routes/garage.lua"] = File.ReadAllText(Path.Combine(uis, "routes", "garage.luau")),
            ["uis/routes/racehud.lua"] = File.ReadAllText(Path.Combine(uis, "routes", "racehud.luau")),
            ["uis/routes/test.lua"] = File.ReadAllText(Path.Combine(uis, "routes", "test.luau")),
            ["uis/components/glasscard.lua"] = File.ReadAllText(Path.Combine(uis, "components", "glasscard.luau")),
            ["uis/components/settings.lua"] = File.ReadAllText(Path.Combine(uis, "components", "settings.luau")),
            ["uis/components/pausemenu.lua"] = File.ReadAllText(Path.Combine(uis, "components", "pausemenu.luau")),
            ["uis/components/devtools.lua"] = File.ReadAllText(Path.Combine(uis, "components", "devtools.luau")),
            ["uis/components/checkbox.lua"] = File.ReadAllText(Path.Combine(uis, "components", "checkbox.luau")),
            ["uis/components/dropdown.lua"] = File.ReadAllText(Path.Combine(uis, "components", "dropdown.luau")),
            ["uis/components/slider.lua"] = File.ReadAllText(Path.Combine(uis, "components", "slider.luau")),
            ["uis/components/toggleswitch.lua"] = File.ReadAllText(Path.Combine(uis, "components", "toggleswitch.luau")),
            ["uis/components/radio.lua"] = File.ReadAllText(Path.Combine(uis, "components", "radio.luau")),
            ["uis/components/textfield.lua"] = File.ReadAllText(Path.Combine(uis, "components", "textfield.luau")),
            ["library/sx/signals.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "signals.luau")),
            ["library/sx/host.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "host.luau")),
            ["library/sx/h.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "h.luau")),
            ["library/sx/types.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "types.luau")),
            ["library/sx/dom.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "dom.luau")),
            ["library/sx/styled.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "styled.luau")),
            ["library/sx/index.lua"] = File.ReadAllText(Path.Combine(lib, "sx", "index.luau")),
            ["library/ui/theme.lua"] = File.ReadAllText(Path.Combine(lib, "ui", "theme.luau")),
            ["library/ui/button.lua"] = File.ReadAllText(Path.Combine(lib, "ui", "button.luau")),
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
        var values = new LuaValue[result.Length];
        for (var i = 0; i < result.Length; i++)
        {
            values[i] = result[i];
        }

        return values;
    }

    /// <summary>
    /// Fake UiLib for the port tests: full host with onEvent handler storage (events),
    /// Lua->C# call recording (calls), and activeRoot capture.
    /// </summary>
    const string PortUiLib = """
        _G.UiLib = {
          instances = 0, appends = 0, removals = 0, setProps = 0, commits = 0,
          activeRoot = nil,
          events = {},
          calls = {},
          createRoot = function() return { type = 'root', children = {} } end,
          createInstance = function(vtype, props)
            _G.UiLib.instances = _G.UiLib.instances + 1
            return { type = vtype, props = props, children = {} }
          end,
          createTextInstance = function(text)
            _G.UiLib.instances = _G.UiLib.instances + 1
            return { type = 'text', text = text, children = {} }
          end,
          appendChild = function(parent, child)
            _G.UiLib.appends = _G.UiLib.appends + 1
            parent.children[#parent.children + 1] = child
          end,
          insertBefore = function(parent, child, before)
            _G.UiLib.appends = _G.UiLib.appends + 1
            local idx = nil
            for i, c in ipairs(parent.children) do
              if c == before then idx = i break end
            end
            if idx == nil then error('insertBefore: before node is not a child of parent') end
            table.insert(parent.children, idx, child)
          end,
          removeChild = function(parent, child)
            _G.UiLib.removals = _G.UiLib.removals + 1
            for i, c in ipairs(parent.children) do
              if c == child then table.remove(parent.children, i) return end
            end
          end,
          setProperty = function(inst, key, value)
            _G.UiLib.setProps = _G.UiLib.setProps + 1
          end,
          commitTextUpdate = function(textInst, oldText, newText)
            _G.UiLib.commits = _G.UiLib.commits + 1
            textInst.text = newText
          end,
          setActiveRoot = function(root) _G.UiLib.activeRoot = root end,
          defer = function(fn) fn() end,
          onEvent = function(event, callback)
            local list = _G.UiLib.events[event]
            if list == nil then
              list = {}
              _G.UiLib.events[event] = list
            end
            list[#list + 1] = callback
            return function()
              for i = #list, 1, -1 do
                if list[i] == callback then table.remove(list, i) end
              end
            end
          end,
          call = function(method, payload)
            _G.UiLib.calls[#_G.UiLib.calls + 1] = { method = method, payload = payload }
          end,
        }
        """ + "\n";

    /// <summary>
    /// Tree-walking helpers for the port tests (visible texts + clickable button lookup).
    /// </summary>
    const string PortHelpers = """
        local function collectTexts(node)
          local out = {}
          local function walk(n)
            if n.type == 'text' and n.text ~= '' then out[#out + 1] = n.text end
            for _, c in ipairs(n.children) do walk(c) end
          end
          walk(node)
          return out
        end

        -- dispatch a C#->Lua event to every subscriber (mirrors UiRenderer.PushToLua)
        local function emit(event, payload)
          local list = _G.UiLib.events[event]
          if list ~= nil then
            for _, cb in ipairs(list) do
              cb(payload)
            end
          end
        end

        -- find a clickable view whose subtree contains the given label
        local function findButton(node, label)
          if node.type == 'view' and type(node.props.onmousedown) == 'function' then
            for _, t in ipairs(collectTexts(node)) do
              if t == label then return node end
            end
          end
          for _, c in ipairs(node.children or {}) do
            local r = findButton(c, label)
            if r then return r end
          end
          return nil
        end
        """ + "\n";

    [Test]
    public void Router_MainMenu_Port_RendersAndNavigates()
    {
        var main = PortUiLib + PortHelpers + """
            require('./uis/router')
            local root = _G.UiLib.activeRoot
            local function texts() return table.concat(collectTexts(root), ',') end

            local initial = texts()
            -- account arrives from the C# phase
            emit('main-menu:account', { name = 'Maxine', isLoggedIn = true })
            local withAccount = texts()
            -- click PLAY -> push submenu
            local playBtn = findButton(root, 'PLAY')
            playBtn.props.onmousedown()
            local playPage = texts()
            -- click BACK -> pop
            local backBtn = findButton(root, 'BACK')
            backBtn.props.onmousedown()
            local back = texts()
            -- unhandled C# navigation falls back to the default page
            emit('nfmw:navigate', 'bogus-route')
            local afterNav = texts()
            return initial, withAccount, playPage, back, afterNav
            """;

        var result = RunPort(main);
        var initial = result[0].Read<string>();
        var withAccount = result[1].Read<string>();
        var playPage = result[2].Read<string>();
        var back = result[3].Read<string>();
        var afterNav = result[4].Read<string>();

        TestContext.Progress.WriteLine($"initial    = {initial}");
        TestContext.Progress.WriteLine($"withAccount= {withAccount}");
        TestContext.Progress.WriteLine($"playPage   = {playPage}");
        TestContext.Progress.WriteLine($"back       = {back}");
        TestContext.Progress.WriteLine($"afterNav   = {afterNav}");

        Assert.That(initial, Does.Contain("NFM WORLD"), "title renders");
        Assert.That(initial, Does.Contain("GARAGE"), "menu item renders");
        Assert.That(initial, Does.Contain("NFM World — Lua + React UI"), "footer renders on root page");
        Assert.That(initial, Does.Not.Contain("Welcome,"), "no welcome before account");
        Assert.That(withAccount, Does.Contain("Welcome, Maxine"), "welcome subtitle after account");
        Assert.That(playPage, Does.Contain("SINGLEPLAYER"), "PLAY pushes the singleplayer menu");
        Assert.That(playPage, Does.Not.Contain("NFM WORLD"), "main page popped when submenu shown");
        Assert.That(playPage, Does.Not.Contain("NFM World — Lua + React UI"), "footer hidden on submenu");
        Assert.That(back, Does.Contain("NFM WORLD"), "BACK returns to the main page");
        Assert.That(afterNav, Does.Contain("NFM WORLD"), "unhandled route falls back to the default page");
    }

    [Test]
    public void AllRoutes_Port_RenderAndNavigate()
    {
        var main = PortUiLib + PortHelpers + """
            require('./uis/router')
            local root = _G.UiLib.activeRoot
            local function texts() return table.concat(collectTexts(root), ',') end

            -- garage: navigate + push collections + currentCar (plain Lua payloads)
            emit('nfmw:navigate', 'garage')
            local garageEmpty = texts()
            emit('garage:collections', {
              collections = {
                { id = 'Tuner', cars = {
                    { name = 'Skyline', fileName = 'sky.rad' },
                    { name = 'Silvia', fileName = 'sil.rad' },
                } },
              },
            })
            emit('garage:currentCar', {
              fileName = 'sky.rad', name = 'Skyline',
              topSpeed = 0.8, acceleration = 0.7, handling = 0.6, powerSave = 0.5,
              strength = 0.4, maxHealth = 0.3, stunting = 0.2, hypergliding = 0.1, abing = 0.9,
            })
            local garageLoaded = texts()

            -- race: navigate + push telemetry
            emit('nfmw:navigate', 'race')
            emit('race:hudState', {
              speed = 50, power = 0.8, damage = 0.1, lapDiffMs = 100, lastLapDiffMs = 50,
              position = 1, totalRacers = 4, countdownTimer = 0, stateText = '', stateTextEndsAt = nil,
              lap = 1, totalLaps = 3, lapTime = 0, chkDiffMs = 0, lastChkDiffMs = 0,
            })
            local race = texts()

            -- test
            emit('nfmw:navigate', 'test')
            local test = texts()

            -- back to main menu
            emit('nfmw:navigate', 'main-menu')
            local backMain = texts()

            return garageEmpty, garageLoaded, race, test, backMain
            """;

        var result = RunPort(main);
        var garageEmpty = result[0].Read<string>();
        var garageLoaded = result[1].Read<string>();
        var race = result[2].Read<string>();
        var test = result[3].Read<string>();
        var backMain = result[4].Read<string>();

        TestContext.Progress.WriteLine($"garageEmpty = {garageEmpty}");
        TestContext.Progress.WriteLine($"garageLoaded= {garageLoaded}");
        TestContext.Progress.WriteLine($"race        = {race}");
        TestContext.Progress.WriteLine($"test        = {test}");
        TestContext.Progress.WriteLine($"backMain    = {backMain}");

        Assert.That(garageEmpty, Does.Contain("Garage"), "garage header renders");
        Assert.That(garageEmpty, Does.Contain("Select a car to view stats"), "placeholder before car selected");
        Assert.That(garageLoaded, Does.Contain("Tuner"), "collection section renders");
        Assert.That(garageLoaded, Does.Contain("Skyline"), "car card renders");
        Assert.That(garageLoaded, Does.Contain("Top Speed"), "stats card renders");
        Assert.That(garageLoaded, Does.Not.Contain("Select a car to view stats"), "stats shown once a car is selected");
        Assert.That(race, Does.Contain("KM/H"), "speed unit renders");
        Assert.That(race, Does.Contain("POSITION"), "position label renders");
        Assert.That(race, Does.Contain("1/4"), "position/total renders");
        Assert.That(test, Does.Contain("CEF + Preact Test"), "test label renders");
        Assert.That(test, Does.Contain("0"), "counter starts at 0");
        Assert.That(backMain, Does.Contain("NFM WORLD"), "navigating back to main-menu renders the menu");
    }

    [Test]
    public void Show_Remount_InnerFor_DoesNotRunStaleEffects()
    {
        // Regression: a Show whose `when` reads a changing signal, with an inner For that
        // also reads it. When the value changes, the Show remounts its content and the old
        // inner For effect is disposed — but it was already queued STALE, so it must be
        // skipped (disposed flag), NOT run after its tree (and anchor) was torn down.
        // Without the fix the stale For effect calls insertBefore with a removed anchor,
        // which the real Yoga host rejects (ArgumentOutOfRangeException).
        var main = PortUiLib + PortHelpers + """
            local Sx = require('./library/sx/index')
            local x = Sx.x

            local text, setText = Sx.createSignal("")
            Sx.render(x(Sx.Show) {
              when = function() return text() ~= "" end,
              x("view") {
                x(Sx.For) {
                  each = function() return { text() } end,
                  function(item) return x("text") { item() } end,
                },
              },
            })
            local root = _G.UiLib.activeRoot

            setText("Starting in 3")
            local s3 = table.concat(collectTexts(root), ',')
            setText("Starting in 2")
            local s2 = table.concat(collectTexts(root), ',')
            setText("Starting in 1")
            local s1 = table.concat(collectTexts(root), ',')
            setText("")
            local empty = table.concat(collectTexts(root), ',')

            return s3, s2, s1, empty
            """;

        var result = RunPort(main);
        var s3 = result[0].Read<string>();
        var s2 = result[1].Read<string>();
        var s1 = result[2].Read<string>();
        var empty = result[3].Read<string>();

        TestContext.Progress.WriteLine($"s3={s3} s2={s2} s1={s1} empty='{empty}'");

        Assert.That(s3, Is.EqualTo("Starting in 3"), "countdown text mounts");
        Assert.That(s2, Is.EqualTo("Starting in 2"), "countdown updates without stale-effect crash");
        Assert.That(s1, Is.EqualTo("Starting in 1"), "second update also clean");
        Assert.That(empty, Is.EqualTo(""), "content unmounts when the Show's when goes false");
    }

    [Test]
    public void RaceHud_PauseMenu_Toggles()
    {
        // Exercises the PauseMenu Show mounted after several component siblings (a trailing
        // TextNode anchor). With the old host this insertBefore computed an all-children
        // index larger than the Yoga child count -> ArgumentOutOfRangeException. The fake
        // host's strict insertBefore (before-not-found) guards the renderer side.
        var main = PortUiLib + PortHelpers + """
            require('./uis/router')
            local root = _G.UiLib.activeRoot
            local function texts() return table.concat(collectTexts(root), ',') end

            emit('nfmw:navigate', 'race')
            local race = texts()
            emit('race:paused', true)
            local paused = texts()
            emit('race:paused', false)
            local resumed = texts()
            emit('nfmw:navigate', 'main-menu')
            local backMain = texts()

            return race, paused, resumed, backMain
            """;

        var result = RunPort(main);
        var race = result[0].Read<string>();
        var paused = result[1].Read<string>();
        var resumed = result[2].Read<string>();
        var backMain = result[3].Read<string>();

        TestContext.Progress.WriteLine($"race={race} paused={paused} resumed={resumed} backMain={backMain}");

        Assert.That(race, Does.Contain("KM/H"), "HUD renders on race");
        Assert.That(paused, Does.Contain("Paused"), "pause menu overlays");
        Assert.That(paused, Does.Contain("Resume"), "resume button");
        Assert.That(resumed, Does.Not.Contain("Paused"), "pause menu hides on resume");
        Assert.That(backMain, Does.Contain("NFM WORLD"), "navigation away disposes the HUD");
    }

    [Test]
    public void Settings_Port_Mounts_Loading()
    {
        var main = PortUiLib + PortHelpers + """
            local Sx = require('./library/sx/index')
            local x = Sx.x
            local Settings = require('./uis/components/settings')

            Sx.render(x(Settings) { onClose = function() end })
            local root = _G.UiLib.activeRoot
            return table.concat(collectTexts(root), ',')
            """;

        var result = RunPort(main);
        var texts = result[0].Read<string>();

        TestContext.Progress.WriteLine($"settings = {texts}");
        Assert.That(texts, Does.Contain("Settings"), "settings header renders");
        Assert.That(texts, Does.Contain("Loading settings..."), "loading state until config/options arrive");
    }
}
