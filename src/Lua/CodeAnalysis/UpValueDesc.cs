namespace Lua.CodeAnalysis;

public record struct UpValueDesc
{
    public string Name;
    public bool IsLocal;
    public int Index;

    /// <summary>
    /// True when the captured local is never assigned after its declaration (nor by any
    /// nested function), so the closure can copy the value at creation time instead of
    /// sharing an <see cref="Runtime.UpValue"/> cell. Only meaningful when
    /// <see cref="IsLocal"/> is set; captures of an enclosing function's upvalue copy the
    /// parent's slot, whatever shape it has.
    /// </summary>
    public bool ByValue;
}
