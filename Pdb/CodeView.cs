namespace PdbExplorer.Pdb;

// Member names follow cvinfo.h so they can be searched for in the Microsoft and LLVM documentation.

internal enum LeafKind : ushort
{
    LF_VTSHAPE = 0x000a,
    LF_LABEL = 0x000e,
    LF_MODIFIER = 0x1001,
    LF_POINTER = 0x1002,
    LF_PROCEDURE = 0x1008,
    LF_MFUNCTION = 0x1009,
    LF_ARGLIST = 0x1201,
    LF_FIELDLIST = 0x1203,
    LF_BITFIELD = 0x1205,
    LF_METHODLIST = 0x1206,
    LF_BCLASS = 0x1400,
    LF_VBCLASS = 0x1401,
    LF_IVBCLASS = 0x1402,
    LF_INDEX = 0x1404,
    LF_VFUNCTAB = 0x1409,
    LF_VFUNCOFF = 0x140c,
    LF_ENUMERATE = 0x1502,
    LF_ARRAY = 0x1503,
    LF_CLASS = 0x1504,
    LF_STRUCTURE = 0x1505,
    LF_UNION = 0x1506,
    LF_ENUM = 0x1507,
    LF_PRECOMP = 0x1509,
    LF_MEMBER = 0x150d,
    LF_STMEMBER = 0x150e,
    LF_METHOD = 0x150f,
    LF_NESTTYPE = 0x1510,
    LF_ONEMETHOD = 0x1511,
    LF_NESTTYPEEX = 0x1512,
    LF_TYPESERVER2 = 0x1515,
    LF_INTERFACE = 0x1519,
    LF_VFTABLE = 0x151d,
    LF_FUNC_ID = 0x1601,
    LF_MFUNC_ID = 0x1602,
    LF_BUILDINFO = 0x1603,
    LF_SUBSTR_LIST = 0x1604,
    LF_STRING_ID = 0x1605,
    LF_UDT_SRC_LINE = 0x1606,
    LF_UDT_MOD_SRC_LINE = 0x1607,
    LF_CLASS2 = 0x1608,
    LF_STRUCTURE2 = 0x1609,
    LF_UNION2 = 0x160a,
    LF_INTERFACE2 = 0x160b,
}

internal enum SymbolKind : ushort
{
    S_END = 0x0006,
    S_FRAMEPROC = 0x1012,
    S_ANNOTATION = 0x1019,
    S_OBJNAME = 0x1101,
    S_THUNK32 = 0x1102,
    S_BLOCK32 = 0x1103,
    S_WITH32 = 0x1104,
    S_LABEL32 = 0x1105,
    S_REGISTER = 0x1106,
    S_CONSTANT = 0x1107,
    S_UDT = 0x1108,
    S_BPREL32 = 0x110b,
    S_LDATA32 = 0x110c,
    S_GDATA32 = 0x110d,
    S_PUB32 = 0x110e,
    S_LPROC32 = 0x110f,
    S_GPROC32 = 0x1110,
    S_REGREL32 = 0x1111,
    S_LTHREAD32 = 0x1112,
    S_GTHREAD32 = 0x1113,
    S_COMPILE2 = 0x1116,
    S_UNAMESPACE = 0x1124,
    S_PROCREF = 0x1125,
    S_DATAREF = 0x1126,
    S_LPROCREF = 0x1127,
    S_ANNOTATIONREF = 0x1128,
    S_TOKENREF = 0x1129,
    S_TRAMPOLINE = 0x112c,
    S_SEPCODE = 0x1132,
    S_SECTION = 0x1136,
    S_COFFGROUP = 0x1137,
    S_EXPORT = 0x1138,
    S_CALLSITEINFO = 0x1139,
    S_FRAMECOOKIE = 0x113a,
    S_DISCARDED = 0x113b,
    S_COMPILE3 = 0x113c,
    S_ENVBLOCK = 0x113d,
    S_LOCAL = 0x113e,
    S_DEFRANGE = 0x113f,
    S_DEFRANGE_SUBFIELD = 0x1140,
    S_DEFRANGE_REGISTER = 0x1141,
    S_DEFRANGE_FRAMEPOINTER_REL = 0x1142,
    S_DEFRANGE_SUBFIELD_REGISTER = 0x1143,
    S_DEFRANGE_FRAMEPOINTER_REL_FULL_SCOPE = 0x1144,
    S_DEFRANGE_REGISTER_REL = 0x1145,
    S_LPROC32_ID = 0x1146,
    S_GPROC32_ID = 0x1147,
    S_BUILDINFO = 0x114c,
    S_INLINESITE = 0x114d,
    S_INLINESITE_END = 0x114e,
    S_PROC_ID_END = 0x114f,
    S_FILESTATIC = 0x1153,
    S_LPROC32_DPC = 0x1155,
    S_LPROC32_DPC_ID = 0x1156,
    S_ARMSWITCHTABLE = 0x1159,
    S_CALLEES = 0x115a,
    S_CALLERS = 0x115b,
    S_POGODATA = 0x115c,
    S_INLINESITE2 = 0x115d,
    S_HEAPALLOCSITE = 0x115e,
    S_FASTLINK = 0x1167,
    S_INLINEES = 0x1168,
}

internal static class CodeViewExtensions
{
    public static string DisplayName(this LeafKind kind) =>
        Enum.IsDefined(kind) ? kind.ToString() : $"LF_0x{(ushort)kind:X4}";

    public static string DisplayName(this SymbolKind kind) =>
        Enum.IsDefined(kind) ? kind.ToString() : $"S_0x{(ushort)kind:X4}";

    public static bool IsUdt(this LeafKind kind) =>
        kind is LeafKind.LF_CLASS or LeafKind.LF_STRUCTURE or LeafKind.LF_UNION or LeafKind.LF_ENUM or LeafKind.LF_INTERFACE;

    /// <summary>C++ keyword that introduces a user-defined type of this kind.</summary>
    public static string Keyword(this LeafKind kind) => kind switch
    {
        LeafKind.LF_CLASS => "class",
        LeafKind.LF_UNION => "union",
        LeafKind.LF_ENUM => "enum",
        LeafKind.LF_INTERFACE => "interface",
        _ => "struct",
    };

    public static bool IsProcedure(this SymbolKind kind) =>
        kind is SymbolKind.S_GPROC32 or SymbolKind.S_LPROC32
            or SymbolKind.S_GPROC32_ID or SymbolKind.S_LPROC32_ID
            or SymbolKind.S_LPROC32_DPC or SymbolKind.S_LPROC32_DPC_ID;

    /// <summary>The procedure's type field is an IPI id instead of a TPI type index.</summary>
    public static bool HasIdType(this SymbolKind kind) =>
        kind is SymbolKind.S_GPROC32_ID or SymbolKind.S_LPROC32_ID or SymbolKind.S_LPROC32_DPC_ID;

    public static bool IsGlobalProcedure(this SymbolKind kind) =>
        kind is SymbolKind.S_GPROC32 or SymbolKind.S_GPROC32_ID;

    public static bool IsData(this SymbolKind kind) =>
        kind is SymbolKind.S_GDATA32 or SymbolKind.S_LDATA32 or SymbolKind.S_GTHREAD32 or SymbolKind.S_LTHREAD32;

    public static bool IsStaticData(this SymbolKind kind) =>
        kind is SymbolKind.S_LDATA32 or SymbolKind.S_LTHREAD32;

    public static bool IsThreadLocalData(this SymbolKind kind) =>
        kind is SymbolKind.S_GTHREAD32 or SymbolKind.S_LTHREAD32;

    public static bool IsInlineSite(this SymbolKind kind) =>
        kind is SymbolKind.S_INLINESITE or SymbolKind.S_INLINESITE2;

    public static bool OpensScope(this SymbolKind kind) =>
        kind.IsProcedure() || kind.IsInlineSite()
            || kind is SymbolKind.S_BLOCK32 or SymbolKind.S_THUNK32 or SymbolKind.S_SEPCODE or SymbolKind.S_WITH32;

    public static bool ClosesScope(this SymbolKind kind) =>
        kind is SymbolKind.S_END or SymbolKind.S_INLINESITE_END or SymbolKind.S_PROC_ID_END;

    /// <summary>One of the S_DEFRANGE_* records that follow an S_LOCAL and say where it lives.</summary>
    public static bool IsDefinitionRange(this SymbolKind kind) =>
        kind is >= SymbolKind.S_DEFRANGE and <= SymbolKind.S_DEFRANGE_REGISTER_REL;
}
