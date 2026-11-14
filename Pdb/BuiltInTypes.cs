namespace PdbExplorer.Pdb;

/// <summary>
/// Type indices below 0x1000 are not records: the low byte selects a built-in type
/// and bits 8-11 say whether it is the type itself or a pointer to it.
/// </summary>
internal static class BuiltInTypes
{
    const uint NullPtr = 0x0103;

    public static string Name(uint typeIndex)
    {
        if (typeIndex == NullPtr)
        {
            return "std::nullptr_t";
        }

        string name = (typeIndex & 0xFF) switch
        {
            0x00 => "<no type>",
            0x03 => "void",
            0x07 => "<not translated>",
            0x08 => "HRESULT",
            0x10 => "signed char",
            0x20 => "unsigned char",
            0x70 => "char",
            0x71 => "wchar_t",
            0x7a => "char16_t",
            0x7b => "char32_t",
            0x7c => "char8_t",
            0x11 => "short",
            0x21 => "unsigned short",
            0x12 => "long",
            0x22 => "unsigned long",
            0x13 or 0x76 => "__int64",
            0x23 or 0x77 => "unsigned __int64",
            0x14 or 0x78 => "__int128",
            0x24 or 0x79 => "unsigned __int128",
            0x68 => "__int8",
            0x69 => "unsigned __int8",
            0x72 => "__int16",
            0x73 => "unsigned __int16",
            0x74 => "int",
            0x75 => "unsigned int",
            0x40 => "float",
            0x41 => "double",
            0x42 => "long double",
            0x43 => "__float128",
            0x46 => "__float16",
            0x30 => "bool",
            0x31 => "bool16",
            0x32 => "bool32",
            0x33 => "bool64",
            var kind => $"<simple 0x{kind:X2}>",
        };
        return PointerMode(typeIndex) != 0 ? name + "*" : name;
    }

    public static long Size(uint typeIndex) => PointerMode(typeIndex) switch
    {
        0 => (typeIndex & 0xFF) switch
        {
            0x10 or 0x20 or 0x70 or 0x7c or 0x68 or 0x69 or 0x30 => 1,
            0x11 or 0x21 or 0x71 or 0x7a or 0x72 or 0x73 or 0x31 or 0x46 => 2,
            0x12 or 0x22 or 0x7b or 0x74 or 0x75 or 0x40 or 0x32 or 0x08 => 4,
            0x13 or 0x23 or 0x76 or 0x77 or 0x41 or 0x33 => 8,
            0x42 => 10,
            0x14 or 0x24 or 0x78 or 0x79 or 0x43 => 16,
            _ => 0,
        },
        6 => 8,   // 64-bit pointer
        7 => 16,
        1 => 2,
        _ => 4,
    };

    static uint PointerMode(uint typeIndex) => (typeIndex >> 8) & 0xF;
}

internal static class Registers
{
    static readonly string[] Qword = ["rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp"];
    static readonly string[] Dword = ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi"];
    static readonly string[] Word = ["ax", "cx", "dx", "bx", "sp", "bp", "si", "di"];
    static readonly string[] Byte = ["al", "cl", "dl", "bl", "ah", "ch", "dh", "bh"];

    /// <summary>Name of a CV_AMD64_* register id.</summary>
    public static string Amd64(ushort id) => id switch
    {
        >= 328 and <= 335 => Qword[id - 328],
        >= 336 and <= 343 => "r" + (id - 328),
        >= 344 and <= 351 => "r" + (id - 336) + "b",
        >= 352 and <= 359 => "r" + (id - 344) + "w",
        >= 360 and <= 367 => "r" + (id - 352) + "d",
        >= 17 and <= 24 => Dword[id - 17],
        >= 9 and <= 16 => Word[id - 9],
        >= 1 and <= 8 => Byte[id - 1],
        324 => "sil",
        325 => "dil",
        326 => "bpl",
        327 => "spl",
        33 => "rip",
        >= 154 and <= 161 => "xmm" + (id - 154),
        >= 252 and <= 259 => "xmm" + (id - 244),
        _ => "reg" + id,
    };
}
