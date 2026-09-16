using System.Text;
using Lua.Runtime;

namespace Lua.Tests.CompilerEquivalence;

/// <summary>
/// Deterministic, human-readable dump of a compiled <see cref="Prototype"/> tree, used as the
/// comparison format for the compiler-equivalence golden tests (see
/// <see cref="CorpusGoldenTests"/>). This exists to give the Milestone 0..2 compiler rewrite
/// (see the compiler-rewrite plan) an objective "is the emitted bytecode byte-for-byte
/// identical" signal that the rest of the test suite -- which is entirely behavioral -- cannot
/// provide on its own.
/// </summary>
static class BytecodeDump
{
    /// <param name="includeLines">
    /// True for the full dump (used by <see cref="CorpusGoldenTests"/>, which tracks the
    /// existing single-pass compiler's own output for regressions over time -- line numbers
    /// matter there since nothing about that compiler is changing).
    /// False for a structural-only dump (opcodes/operands/counts, no per-instruction line
    /// annotations) -- used to compare the AST + <see cref="CodeGenerator"/> pipeline against
    /// the original compiler. The two compilers deliberately do not aim for byte-identical line
    /// numbers: the original's line stamping is a side effect of its scanner's token-lookahead
    /// timing (an implementation quirk, not a deliberate design), whereas CodeGenerator adopts
    /// Luau's model of explicit, AST-position-derived stamps. See the compiler-rewrite plan's
    /// notes on this for the full reasoning.
    /// </param>
    public static string Dump(Prototype root, bool includeLines = true)
    {
        var sb = new StringBuilder();
        DumpPrototype(sb, root, 0, includeLines);
        return sb.ToString();
    }

    static void DumpPrototype(StringBuilder sb, Prototype proto, int depth, bool includeLines)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent)
            .Append("proto ")
            .Append(proto.Name ?? "?")
            .Append(" params=")
            .Append(proto.ParameterCount)
            .Append(" vararg=")
            .Append(proto.HasVariableArguments)
            .Append(" maxstack=")
            .Append(proto.MaxStackSize)
            .Append(" linedefined=")
            .Append(proto.LineDefined)
            .Append('-')
            .Append(proto.LastLineDefined)
            .Append('\n');

        sb.Append(indent).Append("constants:\n");
        var constants = proto.Constants;
        for (var i = 0; i < constants.Length; i++)
        {
            sb.Append(indent).Append("  [").Append(i).Append("] ").Append(constants[i].ToString()).Append('\n');
        }

        // Milestone 4's table templates, which DupTable copies from. Printed only when non-empty,
        // so a prototype without a fused literal keeps the dump -- and therefore the golden file --
        // byte-identical to what it was before this section existed, and the goldens stay a tight
        // diff. The array part is printed whole (nils included: they are slots, and their count is
        // capacity), then the hash entries in insertion order, which is the order `next` exposes.
        var templates = proto.Templates;
        if (templates.Length != 0)
        {
            sb.Append(indent).Append("templates:\n");
            for (var i = 0; i < templates.Length; i++)
            {
                var template = templates[i];
                sb.Append(indent).Append("  [").Append(i).Append("] array=[");
                var array = template.GetArraySpan();
                for (var v = 0; v < array.Length; v++)
                {
                    if (v != 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(array[v].ToString());
                }

                sb.Append("] hash={");
                var entries = new List<KeyValuePair<LuaValue, LuaValue>>();
                template.CopyHashEntriesTo(entries);
                for (var e = 0; e < entries.Count; e++)
                {
                    if (e != 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(entries[e].Key.ToString()).Append('=').Append(entries[e].Value.ToString());
                }

                sb.Append("}\n");
            }
        }

        sb.Append(indent).Append("upvalues:\n");
        var upValues = proto.UpValues;
        for (var i = 0; i < upValues.Length; i++)
        {
            var u = upValues[i];
            sb.Append(indent)
                .Append("  [")
                .Append(i)
                .Append("] ")
                .Append(u.Name)
                .Append(" local=")
                .Append(u.IsLocal)
                .Append(" index=")
                .Append(u.Index)
                .Append(" byvalue=")
                .Append(u.ByValue)
                .Append('\n');
        }

        sb.Append(indent).Append("locals:\n");
        var locals = proto.LocalVariables;
        for (var i = 0; i < locals.Length; i++)
        {
            var l = locals[i];
            sb.Append(indent)
                .Append("  [")
                .Append(i)
                .Append("] ")
                .Append(l.Name)
                .Append(" pc=")
                .Append(l.StartPc)
                .Append('-')
                .Append(l.EndPc)
                .Append('\n');
        }

        sb.Append(indent).Append("code:\n");
        var code = proto.Code;
        var lineInfo = proto.LineInfo;
        for (var i = 0; i < code.Length; i++)
        {
            sb.Append(indent).Append("  ").Append(i).Append(": ");
            if (includeLines)
            {
                sb.Append("[line ").Append(i < lineInfo.Length ? lineInfo[i] : -1).Append("] ");
            }

            sb.Append(code[i].ToString()).Append('\n');
        }

        var children = proto.ChildPrototypes;
        for (var i = 0; i < children.Length; i++)
        {
            sb.Append(indent).Append("child ").Append(i).Append(":\n");
            DumpPrototype(sb, children[i], depth + 1, includeLines);
        }
    }
}
