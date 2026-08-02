namespace Lua.SourceGenerator;

class TempCollections
{
    public readonly HashSet<LuaObjectMetamethod> Metamethods = new();
    public readonly List<string> InvalidMemberNames = new();
    public readonly List<AliasEntry> AliasProperties = new();

    public void Clear()
    {
        Metamethods.Clear();
        InvalidMemberNames.Clear();
        AliasProperties.Clear();
    }

    public struct AliasEntry
    {
        public string AliasName;
        public string FunctionName;
    }
}
