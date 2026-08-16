using Lua.Standard;

namespace Lua.Tests;

[LuaObject]
public partial class LuaTestObj
{
    int x;
    int y;

    public LuaTestObj()
    {
    }

    [LuaMember("new")]
    public LuaTestObj(int x, int y)
    {
        this.x = x;
        this.y = y;
    }

    [LuaMember("x")]
    public int X
    {
        get => x;
        set => x = value;
    }

    [LuaMember("y")]
    public int Y
    {
        get => y;
        set => y = value;
    }

    [LuaMember("create")]
    public static LuaTestObj Create(int x, int y)
    {
        return new LuaTestObj() { x = x, y = y };
    }

    [LuaMetamethod(LuaObjectMetamethod.Add)]
    public static LuaTestObj Add(LuaTestObj a, LuaTestObj b)
    {
        return new LuaTestObj() { x = a.x + b.x, y = a.y + b.y };
    }

    [LuaMetamethod(LuaObjectMetamethod.Sub)]
    public static async Task<LuaTestObj> Sub(LuaTestObj a, LuaTestObj b)
    {
        await Task.Delay(1);
        return new LuaTestObj() { x = a.x - b.x, y = a.y - b.y };
    }

    [LuaMetamethod(LuaObjectMetamethod.Len)]
    public async Task<double> Len()
    {
        await Task.Delay(1);
        return x + y;
    }

    [LuaMetamethod(LuaObjectMetamethod.Unm)]
    public LuaTestObj Unm()
    {
        return new LuaTestObj() { x = -x, y = -y };
    }

    [LuaMember]
    public object GetObj() => this;

    // C# operator overloads — auto-detected as Lua metamethods.
    // operator + conflicts with explicit [LuaMetamethod(Add)] above;
    // the explicit one takes precedence and the operator is silently skipped.
    public static LuaTestObj operator +(LuaTestObj a, LuaTestObj b)
        => new() { x = a.x + b.x, y = a.y + b.y };

    public static LuaTestObj operator *(LuaTestObj a, LuaTestObj b)
        => new() { x = a.x * b.x, y = a.y * b.y };

    public static LuaTestObj operator -(LuaTestObj a)
        => new() { x = -a.x, y = -a.y };

    public static bool operator ==(LuaTestObj a, LuaTestObj b)
        => a.x == b.x && a.y == b.y;

    public static bool operator !=(LuaTestObj a, LuaTestObj b)
        => !(a == b);

    public static bool operator <(LuaTestObj a, LuaTestObj b)
        => (a.x + a.y) < (b.x + b.y);

    public static bool operator >(LuaTestObj a, LuaTestObj b)
        => (a.x + a.y) > (b.x + b.y);

    public static bool operator <=(LuaTestObj a, LuaTestObj b)
        => (a.x + a.y) <= (b.x + b.y);

    public static bool operator >=(LuaTestObj a, LuaTestObj b)
        => (a.x + a.y) >= (b.x + b.y);

    public override bool Equals(object? obj) => obj is LuaTestObj o && this == o;
    public override int GetHashCode() => HashCode.Combine(x, y);
}

[LuaObject]
public partial class TestUserData
{
    [LuaMember]
    public int Property { get; init; }

    [LuaMember]
    public int ReadOnlyProperty { get; }

    [LuaMember]
    public int SetOnlyProperty
    {
        set { }
    }

    [LuaMember]
    public LuaValue LuaValueProperty { get; set; }

    [LuaMember("p2")]
    public string PropertyWithName { get; set; } = "";

    [LuaMember]
    public static void MethodVoid()
    {
        Console.WriteLine("HEY!");
    }

    [LuaMember]
    public static async Task MethodAsync()
    {
        await Task.CompletedTask;
    }

    [LuaMember]
    public static double StaticMethodWithReturnValue(double a, double b)
    {
        Console.WriteLine($"HEY! {a} {b}");
        return a + b;
    }

    [LuaMember]
    public double InstanceMethodWithReturnValue()
    {
        return Property;
    }

    [LuaMember]
    public async ValueTask<LuaValue> InstanceMethodWithReturnValueAsync(
        LuaValue value,
        CancellationToken ct
    )
    {
        await Task.Delay(1, ct);
        return value;
    }

    [LuaMember]
    public static double RefOutTestMethod(int x, ref double a, out string b, out LuaValue c)
    {
        a += x;
        b = $"x:{x}";
        c = a * 2;
        return a / 2;
    }

    [LuaMember]
    public static void OutOnlyTestMethod(int x, out string b, out LuaValue c)
    {
        b = $"value:{x}";
        c = x + 1;
    }

    [LuaMetamethod(LuaObjectMetamethod.Call)]
    public string Call()
    {
        return "Called!";
    }
}

[LuaObject]
public partial class IntArrayUserData
{
    public int[] Array { get; } = new int[10];

    public int this[int index]
    {
        get => Array[index];
        set => Array[index] = value;
    }

    [LuaMember("name")]
    public string Name => "IntArray";

    [LuaMember("length")]
    public int Length => Array.Length;

    [LuaMetamethod(LuaObjectMetamethod.Len)]
    public int GetLength() => Array.Length;

    [LuaMetamethod(LuaObjectMetamethod.Index)]
    public int GetAt(int index)
    {
        return Array[index];
    }

    [LuaMetamethod(LuaObjectMetamethod.NewIndex)]
    public void SetAt(int index, int value)
    {
        Array[index] = value;
    }
}

[LuaObject]
public partial class StringKeyUserData
{
    readonly Dictionary<string, int> values = new();

    [LuaMember("name")]
    public string Name => "StringMap";

    [LuaMember("length")]
    public int Length => values.Count;

    [LuaMetamethod(LuaObjectMetamethod.Index)]
    public int GetAt(string key)
    {
        return values.TryGetValue(key, out var value) ? value : -1;
    }

    [LuaMetamethod(LuaObjectMetamethod.NewIndex)]
    public void SetAt(string key, int value)
    {
        values[key] = value;
    }
}

[LuaObject]
public abstract partial class ParentClass
{
    [LuaMember("value")]
    public string Value { get; set; } = "";

    [LuaMember("getValue")]
    public string GetValue() => $"{GetType().Name}:{Value}";
}

[LuaObject]
public partial class ChildClass : ParentClass
{
    [LuaMember("number")]
    public int Number { get; set; }
}

[LuaObject]
public readonly partial struct LuaTestStruct
{
    readonly float x;
    readonly float y;

    public LuaTestStruct(float x, float y)
    {
        this.x = x;
        this.y = y;
    }

    [LuaMember("x")]
    public float X => x;

    [LuaMember("y")]
    public float Y => y;

    [LuaMember("create")]
    public static LuaTestStruct Create(float x, float y) => new(x, y);

    [LuaMetamethod(LuaObjectMetamethod.Add)]
    public static LuaTestStruct Add(LuaTestStruct a, LuaTestStruct b) =>
        new(a.x + b.x, a.y + b.y);

    [LuaMember("length")]
    public double Length() => Math.Sqrt(x * x + y * y);

    public override string ToString() => $"({x}, {y})";
}

[LuaObject]
public partial interface ILuaTestBaseInterface
{
    [LuaMember("baseName")]
    string BaseName { get; }
}

[LuaObject]
public partial interface ILuaTestInterface : ILuaTestBaseInterface
{
    [LuaMember("name")]
    string Name { get; }

    [LuaMember("value")]
    int Value { get; }

    [LuaMember("create")]
    static ILuaTestInterface Create(string baseName, string name, int value) =>
        new LuaTestImpl(baseName, name, value);
}

public sealed class LuaTestImpl : ILuaTestInterface
{
    public string BaseName { get; }
    public string Name { get; }
    public int Value { get; }

    public LuaTestImpl(string baseName, string name, int value)
    {
        BaseName = baseName;
        Name = name;
        Value = value;
    }
}

[LuaObject]
public partial class LuaCtorDefaultNameObj
{
    [LuaMember]
    public int Value { get; }

    [LuaMember]
    public LuaCtorDefaultNameObj(int value)
    {
        Value = value;
    }
}

[LuaObject]
public partial class NullableArgObj
{
    [LuaMember("label")]
    public string? Label { get; set; }

    [LuaMember("count")]
    public int? Count { get; set; }

    [LuaMember("structProp")]
    public LuaTestStruct? StructProp { get; set; }

    [LuaMember("echoString")]
    public static string? EchoString(string? s) => s;

    [LuaMember("echoInt")]
    public static int? EchoInt(int? x) => x;

    [LuaMember("echoDouble")]
    public static double? EchoDouble(double? x) => x;

    [LuaMember("echoStruct")]
    public static LuaTestStruct? EchoStruct(LuaTestStruct? s) => s;

    [LuaMember("describe")]
    public string? Describe(string? prefix, int? count) =>
        prefix == null ? null : $"{prefix}:{count}";
}

public class LuaObjectTests
{
    [Test]
    public async Task Test_Property()
    {
        var userData = new TestUserData { Property = 1 };

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("return test.Property");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0], Is.EqualTo(new LuaValue(1)));
    }

    [Test]
    public async Task Test_PropertyWithName()
    {
        var userData = new TestUserData { PropertyWithName = "foo" };

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("return test.p2");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0], Is.EqualTo(new LuaValue("foo")));
    }

    [Test]
    public async Task Test_MethodVoid()
    {
        var userData = new TestUserData();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("return test.MethodVoid()");

        Assert.That(results, Has.Length.EqualTo(0));
    }

    [Test]
    public async Task Test_MethodAsync()
    {
        var userData = new TestUserData();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("return test.MethodAsync()");

        Assert.That(results, Has.Length.EqualTo(0));
    }

    [Test]
    public async Task Test_StaticMethodWithReturnValue()
    {
        var userData = new TestUserData();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("return test.StaticMethodWithReturnValue(1, 2)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0], Is.EqualTo(new LuaValue(3)));
    }

    [Test]
    public async Task Test_InstanceMethodWithReturnValue()
    {
        var userData = new TestUserData { Property = 1 };

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("return test:InstanceMethodWithReturnValue()");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0], Is.EqualTo(new LuaValue(1)));
    }

    [Test]
    public async Task Test_InstanceMethodWithReturnValueAsync()
    {
        var userData = new TestUserData { Property = 1 };

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync(
            "return test:InstanceMethodWithReturnValueAsync(2)"
        );

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0], Is.EqualTo(new LuaValue(2)));
    }

    [Test]
    public async Task Test_StaticMethodWithRefAndOutParameters()
    {
        var userData = new TestUserData();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync(
            """
            local ret, a, b, c = test.RefOutTestMethod(4, 1.5)
            return ret, a, b, c
            """
        );

        Assert.That(results, Has.Length.EqualTo(4));
        Assert.That(results[0], Is.EqualTo(new LuaValue(2.75)));
        Assert.That(results[1], Is.EqualTo(new LuaValue(5.5)));
        Assert.That(results[2], Is.EqualTo(new LuaValue("x:4")));
        Assert.That(results[3], Is.EqualTo(new LuaValue(11)));
    }

    [Test]
    public async Task Test_StaticMethodWithOutParametersAndNoReturnValue()
    {
        var userData = new TestUserData();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync(
            """
            local b, c = test.OutOnlyTestMethod(4)
            return b, c
            """
        );

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0], Is.EqualTo(new LuaValue("value:4")));
        Assert.That(results[1], Is.EqualTo(new LuaValue(5)));
    }

    [Test]
    public async Task Test_CallMetamethod()
    {
        var userData = new TestUserData();

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync(
            """
            assert(test() == 'Called!')
            return test()
            """
        );

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0], Is.EqualTo(new LuaValue("Called!")));
    }

    [Test]
    public async Task Test_AbstractBaseLuaObjectMembersAreInherited()
    {
        var userData = new ChildClass { Value = "initial", Number = 7 };

        var state = LuaState.Create();
        state.Environment["child"] = userData;
        var results = await state.DoStringAsync(
            """
            child.value = child.value .. '-updated'
            child.number = child.number + 1
            return child.value, child.number, child:getValue()
            """
        );

        Assert.That(results, Has.Length.EqualTo(3));
        Assert.That(results[0], Is.EqualTo(new LuaValue("initial-updated")));
        Assert.That(results[1], Is.EqualTo(new LuaValue(8)));
        Assert.That(results[2], Is.EqualTo(new LuaValue("ChildClass:initial-updated")));
        Assert.That(userData.Value, Is.EqualTo("initial-updated"));
        Assert.That(userData.Number, Is.EqualTo(8));
    }

    [Test]
    public async Task Test_ArithMetamethod()
    {
        var userData = new LuaTestObj();

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["TestObj"] = userData;
        var results = await state.DoStringAsync(
            """
            local a = TestObj.create(1, 2)
            local b = TestObj.create(3, 4)
            return a + b, a - b
            """
        );
        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0].Read<object>(), Is.TypeOf<LuaTestObj>());
        var objAdd = results[0].Read<LuaTestObj>();
        Assert.That(objAdd.X, Is.EqualTo(4));
        Assert.That(objAdd.Y, Is.EqualTo(6));
        Assert.That(results[1].Read<object>(), Is.TypeOf<LuaTestObj>());
        var objSub = results[1].Read<LuaTestObj>();
        Assert.That(objSub.X, Is.EqualTo(-2));
        Assert.That(objSub.Y, Is.EqualTo(-2));
    }

    [Test]
    public async Task Test_IndexMetamethod()
    {
        var userData = new IntArrayUserData();

        userData[0] = 1;
        userData[1] = 1;
        userData[2] = 2;
        userData[3] = 3;
        userData[4] = 5;

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["TestObj"] = userData;
        var results = await state.DoStringAsync(
            """
            return TestObj[0], TestObj[1], TestObj[2], TestObj[3], TestObj[4] ,TestObj.name, #TestObj
            """
        );
        Assert.That(results, Has.Length.EqualTo(7));
        Assert.That(results[0].TryRead<int>(out var result), Is.True);
        Assert.That(result, Is.EqualTo(1));
        Assert.That(results[1].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(1));
        Assert.That(results[2].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(2));
        Assert.That(results[3].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(3));
        Assert.That(results[4].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(5));
        Assert.That(results[5].TryRead<string>(out var strResult), Is.True);
        Assert.That(strResult, Is.EqualTo("IntArray"));
        Assert.That(results[6].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(10));
    }

    [Test]
    public async Task Test_NewIndexMetamethod()
    {
        var userData = new IntArrayUserData();

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["TestObj"] = userData;
        var results = await state.DoStringAsync(
            """
            TestObj[2] = 8
            return TestObj[2], TestObj.name, #TestObj
            """
        );
        Assert.That(results, Has.Length.EqualTo(3));
        Assert.That(results[0].TryRead<int>(out var result), Is.True);
        Assert.That(result, Is.EqualTo(8));
        Assert.That(results[1].TryRead<string>(out var strResult), Is.True);
        Assert.That(strResult, Is.EqualTo("IntArray"));
        Assert.That(results[2].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(10));
    }

    [Test]
    public async Task Test_StringIndexMetamethod()
    {
        var userData = new StringKeyUserData();

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["TestObj"] = userData;
        var results = await state.DoStringAsync(
            """
            TestObj.answer = 42
            return TestObj.answer, TestObj.name, TestObj.length
            """
        );
        Assert.That(results, Has.Length.EqualTo(3));
        Assert.That(results[0].TryRead<int>(out var result), Is.True);
        Assert.That(result, Is.EqualTo(42));
        Assert.That(results[1].TryRead<string>(out var strResult), Is.True);
        Assert.That(strResult, Is.EqualTo("StringMap"));
        Assert.That(results[2].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    public async Task Test_StringNewIndexMetamethod()
    {
        var userData = new StringKeyUserData();

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["TestObj"] = userData;
        var results = await state.DoStringAsync(
            """
            TestObj.value = 8
            TestObj.other = 13
            return TestObj.value, TestObj.other, TestObj.name, TestObj.length
            """
        );
        Assert.That(results, Has.Length.EqualTo(4));
        Assert.That(results[0].TryRead<int>(out var result), Is.True);
        Assert.That(result, Is.EqualTo(8));
        Assert.That(results[1].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(13));
        Assert.That(results[2].TryRead<string>(out var strResult), Is.True);
        Assert.That(strResult, Is.EqualTo("StringMap"));
        Assert.That(results[3].TryRead<int>(out result), Is.True);
        Assert.That(result, Is.EqualTo(2));
    }

    [Test]
    public async Task Test_LenMetamethod()
    {
        var userData = new LuaTestObj();

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["TestObj"] = userData;
        var results = await state.DoStringAsync(
            """
            function testLen(obj)
                return #obj
            end
            local obj = TestObj.create(1, 2)
            return testLen(TestObj.create(1, 2)),-obj
            """
        );
        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0].Read<double>(), Is.EqualTo(3));
        Assert.That(results[1].Read<object>(), Is.TypeOf<LuaTestObj>());
        var objUnm = results[1].Read<LuaTestObj>();
        Assert.That(objUnm.X, Is.EqualTo(-1));
        Assert.That(objUnm.Y, Is.EqualTo(-2));
    }

    [Test]
    public async Task Test_AutoDetectedOperatorMetamethods()
    {
        var userData = new LuaTestObj();

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["TestObj"] = userData;

        // __mul: auto-detected from operator *
        // __add: still uses explicit [LuaMetamethod(Add)] (operator + silently skipped)
        // __unm: still uses explicit [LuaMetamethod(Unm)] (operator - unary silently skipped)
        // __eq: auto-detected from operator ==
        // __lt: auto-detected from operator <
        // __le: auto-detected from operator <=
        var results = await state.DoStringAsync("""
            local a = TestObj.create(2, 3)
            local b = TestObj.create(4, 1)
            local addResult = a + b
            local mulResult = a * b
            local unmResult = -a
            local eqResult = a == b
            local neResult = a ~= b  -- uses __eq via negation
            local ltResult = a < b
            local leResult = a <= b
            return addResult.x, addResult.y,
                   mulResult.x, mulResult.y,
                   unmResult.x, unmResult.y,
                   eqResult, neResult,
                   ltResult, leResult
            """);

        Assert.That(results, Has.Length.EqualTo(10));

        // add: 2+4=6, 3+1=4
        Assert.That(results[0].TryRead<int>(out var v), Is.True);
        Assert.That(v, Is.EqualTo(6));
        Assert.That(results[1].TryRead<int>(out v), Is.True);
        Assert.That(v, Is.EqualTo(4));

        // mul: 2*4=8, 3*1=3 (auto-detected __mul)
        Assert.That(results[2].TryRead<int>(out v), Is.True);
        Assert.That(v, Is.EqualTo(8));
        Assert.That(results[3].TryRead<int>(out v), Is.True);
        Assert.That(v, Is.EqualTo(3));

        // unm: -2, -3 (uses explicit [LuaMetamethod(Unm)])
        Assert.That(results[4].TryRead<int>(out v), Is.True);
        Assert.That(v, Is.EqualTo(-2));
        Assert.That(results[5].TryRead<int>(out v), Is.True);
        Assert.That(v, Is.EqualTo(-3));

        // eq: false (auto-detected __eq)
        Assert.That(results[6].TryRead<bool>(out var b), Is.True);
        Assert.That(b, Is.False);

        // ne: true (uses __eq)
        Assert.That(results[7].TryRead<bool>(out b), Is.True);
        Assert.That(b, Is.True);

        // lt: 5 < 5 = false (auto-detected __lt)
        Assert.That(results[8].TryRead<bool>(out b), Is.True);
        Assert.That(b, Is.False);

        // le: 5 <= 5 = true (auto-detected __le)
        Assert.That(results[9].TryRead<bool>(out b), Is.True);
        Assert.That(b, Is.True);
    }

    [Test]
    public async Task Test_StructPropertyRead()
    {
        var userData = LuaTestStruct.Create(3, 4);

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("return test.x, test.y");

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0].TryRead<float>(out var x), Is.True);
        Assert.That(x, Is.EqualTo(3));
        Assert.That(results[1].TryRead<float>(out var y), Is.True);
        Assert.That(y, Is.EqualTo(4));
    }

    [Test]
    public void Test_StructPropertyWriteThrows()
    {
        var userData = LuaTestStruct.Create(1, 2);

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        // Struct property setters are not supported — value-type properties
        // are treated as read-only to avoid silently mutating a copy.
        Assert.ThrowsAsync<LuaRuntimeException>(async () =>
        {
            await state.DoStringAsync("test.x = 10");
        });
    }

    [Test]
    public async Task Test_StructInstanceMethod()
    {
        var userData = LuaTestStruct.Create(3, 4);

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("return test:length()");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].TryRead<double>(out var len), Is.True);
        Assert.That(len, Is.EqualTo(5));
    }

    [Test]
    public async Task Test_StructArithMetamethod()
    {
        var userData = default(LuaTestStruct);

        var state = LuaState.Create();
        state.OpenBasicLibrary();
        state.Environment["TestObj"] = userData;
        var results = await state.DoStringAsync("""
            local a = TestObj.create(1, 2)
            local b = TestObj.create(3, 4)
            return a + b
            """);

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Read<object>(), Is.TypeOf<LuaTestStruct>());
        var objAdd = results[0].Read<LuaTestStruct>();
        Assert.That(objAdd.X, Is.EqualTo(4));
        Assert.That(objAdd.Y, Is.EqualTo(6));
    }

    [Test]
    public async Task Test_InterfacePropertyRead()
    {
        var impl = ILuaTestInterface.Create("base", "hello", 42);

        var state = LuaState.Create();
        state.Environment["test"] = LuaValue.FromUserData(impl);
        var results = await state.DoStringAsync("return test.name, test.value");

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0].TryRead<string>(out var name), Is.True);
        Assert.That(name, Is.EqualTo("hello"));
        Assert.That(results[1].TryRead<int>(out var value), Is.True);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public async Task Test_InterfaceInheritedPropertyRead()
    {
        var impl = ILuaTestInterface.Create("baseName", "hello", 42);

        var state = LuaState.Create();
        state.Environment["test"] = LuaValue.FromUserData(impl);
        var results = await state.DoStringAsync("return test.baseName, test.name, test.value");

        Assert.That(results, Has.Length.EqualTo(3));
        Assert.That(results[0].TryRead<string>(out var baseName), Is.True);
        Assert.That(baseName, Is.EqualTo("baseName"));
        Assert.That(results[1].TryRead<string>(out var name), Is.True);
        Assert.That(name, Is.EqualTo("hello"));
        Assert.That(results[2].TryRead<int>(out var value), Is.True);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public async Task Test_InterfaceStaticFactory()
    {
        var state = LuaState.Create();
        state.Environment["TestIface"] = LuaValue.FromUserData(new LuaTestImpl("", "", 0));
        var results = await state.DoStringAsync("""
            local obj = TestIface.create("base", "world", 99)
            return obj.name, obj.value
            """);

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0].TryRead<string>(out var name), Is.True);
        Assert.That(name, Is.EqualTo("world"));
        Assert.That(results[1].TryRead<int>(out var value), Is.True);
        Assert.That(value, Is.EqualTo(99));
    }

    [Test]
    public async Task Test_ConstructorAsStaticMember()
    {
        var state = LuaState.Create();
        state.Environment["TestObj"] = new LuaTestObj();
        var results = await state.DoStringAsync("""
            local obj = TestObj.new(5, 6)
            return obj.x, obj.y
            """);

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0].TryRead<int>(out var x), Is.True);
        Assert.That(x, Is.EqualTo(5));
        Assert.That(results[1].TryRead<int>(out var y), Is.True);
        Assert.That(y, Is.EqualTo(6));
    }

    [Test]
    public async Task Test_ConstructorWithoutExplicitNameDefaultsToNew()
    {
        var state = LuaState.Create();
        state.Environment["TestObj"] = new LuaCtorDefaultNameObj(0);
        var results = await state.DoStringAsync("""
            local obj = TestObj.new(42)
            return obj.Value
            """);

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].TryRead<int>(out var value), Is.True);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public async Task Test_NullableReferenceParam_NilAndValue()
    {
        var userData = new NullableArgObj();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("""
            local a = test.echoString(nil)
            local b = test.echoString("hello")
            return a, b
            """);

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0], Is.EqualTo(LuaValue.Nil));
        Assert.That(results[1].TryRead<string>(out var s), Is.True);
        Assert.That(s, Is.EqualTo("hello"));
    }

    [Test]
    public async Task Test_NullableValueParam_NilAndValue()
    {
        var userData = new NullableArgObj();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("""
            local a = test.echoInt(nil)
            local b = test.echoInt(42)
            local c = test.echoDouble(3.5)
            return a, b, c
            """);

        Assert.That(results, Has.Length.EqualTo(3));
        Assert.That(results[0], Is.EqualTo(LuaValue.Nil));
        Assert.That(results[1].TryRead<int>(out var i), Is.True);
        Assert.That(i, Is.EqualTo(42));
        Assert.That(results[2].TryRead<double>(out var d), Is.True);
        Assert.That(d, Is.EqualTo(3.5));
    }

    [Test]
    public async Task Test_NullableStructParam_NilAndValue()
    {
        var userData = new NullableArgObj();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        state.Environment["LuaTestStruct"] = default(LuaTestStruct);
        var results = await state.DoStringAsync("""
            local a = test.echoStruct(nil)
            local s = LuaTestStruct.create(3, 4)
            local b = test.echoStruct(s)
            return a, b
            """);

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0], Is.EqualTo(LuaValue.Nil));
        Assert.That(results[1].Read<object>(), Is.TypeOf<LuaTestStruct>());
        var structResult = results[1].Read<LuaTestStruct>();
        Assert.That(structResult.X, Is.EqualTo(3));
        Assert.That(structResult.Y, Is.EqualTo(4));
    }

    [Test]
    public async Task Test_NullableInstanceMethod_MixedArgs()
    {
        var userData = new NullableArgObj();

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("""
            local a = test:describe("item", 3)
            local b = test:describe(nil, 3)
            return a, b
            """);

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0].TryRead<string>(out var s), Is.True);
        Assert.That(s, Is.EqualTo("item:3"));
        Assert.That(results[1], Is.EqualTo(LuaValue.Nil));
    }

    [Test]
    public async Task Test_NullableProperty_GetSet()
    {
        var userData = new NullableArgObj { Label = "initial", Count = 7 };

        var state = LuaState.Create();
        state.Environment["test"] = userData;
        var results = await state.DoStringAsync("""
            local a = test.label
            local b = test.count
            test.label = nil
            test.count = nil
            test.count = 42
            test.structProp = nil
            return a, b, test.label, test.count, test.structProp
            """);

        Assert.That(results, Has.Length.EqualTo(5));
        Assert.That(results[0].TryRead<string>(out var label), Is.True);
        Assert.That(label, Is.EqualTo("initial"));
        Assert.That(results[1].TryRead<int>(out var count), Is.True);
        Assert.That(count, Is.EqualTo(7));
        Assert.That(results[2], Is.EqualTo(LuaValue.Nil));
        Assert.That(results[3].TryRead<int>(out count), Is.True);
        Assert.That(count, Is.EqualTo(42));
        Assert.That(results[4], Is.EqualTo(LuaValue.Nil));
        Assert.That(userData.Label, Is.Null);
        Assert.That(userData.Count, Is.EqualTo(42));
        Assert.That(userData.StructProp, Is.Null);
    }
}
