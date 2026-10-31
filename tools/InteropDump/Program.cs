// dotnet run --project tools/InteropDump -- <assembly.dll> <type-name regex> [--members] [--names]
//   --names    only print matching type names (one per line, with field/method counts)
//   --members  also print fields, properties and methods with signatures
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

if (args.Length < 2) { Console.Error.WriteLine("usage: InteropDump <dll> <regex> [--members|--names]"); return 2; }
var rx = new Regex(args[1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
bool members = args.Contains("--members"), namesOnly = args.Contains("--names");

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();
var tp = new Names();

foreach (var handle in md.TypeDefinitions.OrderBy(h => Util.FullName(md, h)))
{
    var t = md.GetTypeDefinition(handle);
    string full = Util.FullName(md, handle);
    if (!rx.IsMatch(full)) continue;

    string baseName = t.BaseType.IsNil ? "" : " : " + Util.BaseName(md, t.BaseType);
    var fields = t.GetFields().Count; var methods = t.GetMethods().Count;
    Console.WriteLine($"{full}{baseName}  [{fields} fields, {methods} methods]");
    if (namesOnly || !members) continue;

    foreach (var fh in t.GetFields())
    {
        var f = md.GetFieldDefinition(fh);
        string name = md.GetString(f.Name);
        if (name.StartsWith("NativeFieldInfoPtr_") || name == "NativeClassPtr") continue; // Il2CppInterop plumbing
        Console.WriteLine($"    field  {f.DecodeSignature(tp, null)} {name}");
    }
    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        string name = md.GetString(m.Name);
        if (name.StartsWith("NativeMethodInfoPtr_") || name == ".cctor") continue;
        var sig = m.DecodeSignature(tp, null);
        var names = m.GetParameters().Select(h => md.GetParameter(h)).Where(p => p.SequenceNumber > 0)
                     .ToDictionary(p => p.SequenceNumber, p => md.GetString(p.Name));
        var ps = sig.ParameterTypes.Select((t, i) => names.TryGetValue(i + 1, out var n) ? $"{t} {n}" : t);
        Console.WriteLine($"    method {sig.ReturnType} {name}({string.Join(", ", ps)})");
    }
}
return 0;

static class Util
{
    public static string FullName(MetadataReader md, TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        var decl = t.GetDeclaringType();
        string name = md.GetString(t.Name);
        if (!decl.IsNil) return FullName(md, decl) + "+" + name;
        string ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    public static string BaseName(MetadataReader md, EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeDefinition => FullName(md, (TypeDefinitionHandle)h),
        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name),
        HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(new Names(), null),
        _ => "?",
    };
}

sealed class Names : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Int32 => "int", PrimitiveTypeCode.Int64 => "long", PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double", PrimitiveTypeCode.Boolean => "bool", PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Void => "void", PrimitiveTypeCode.Byte => "byte", PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt32 => "uint", PrimitiveTypeCode.UInt64 => "ulong", PrimitiveTypeCode.Object => "object",
        _ => c.ToString().ToLowerInvariant(),
    };
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte raw) => Util.FullName(r, h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte raw)
    {
        var tr = r.GetTypeReference(h);
        string ns = r.GetString(tr.Namespace), n = r.GetString(tr.Name);
        return ns.Length > 0 ? ns + "." + n : n;
    }
    public string GetTypeFromSpecification(MetadataReader r, object? ctx, TypeSpecificationHandle h, byte raw) =>
        r.GetTypeSpecification(h).DecodeSignature(this, ctx);
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object? ctx, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? ctx, int i) => "!" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPinnedType(string e) => e;
}
