using PdbExplorer.Pdb;
using static PdbExplorer.Pdb.ByteReading;
using static PdbExplorer.Pdb.LeafKind;

namespace PdbExplorer.Rendering;

// Classes, structs, unions and enums.
internal sealed partial class DocumentRenderer
{
    // State of the member list being written: the access level in effect, and whether the
    // current section already has a member (an access label is preceded by an empty line otherwise).
    int _currentAccess;
    bool _sectionIsEmpty;

    void RenderType(uint requestedType)
    {
        uint typeIndex = _pdb.ResolveForwardReference(requestedType);
        _document.Title = _pdb.TypeName(typeIndex);

        var record = _pdb.Tpi.Contains(typeIndex) ? _pdb.Tpi.Record(typeIndex) : default;
        if (record.IsEmpty || !UdtRecord.TryRead(record, out var udt))
        {
            RenderOtherType(typeIndex, record);
            return;
        }

        string name = Utf8(udt.Name);
        bool isEnum = udt.Kind == LF_ENUM;
        Property("Name", name);
        Property("Kind", udt.Kind.Keyword());
        if (isEnum)
        {
            Property("Underlying type", _pdb.TypeName(udt.UnderlyingType));
        }
        else
        {
            Property("Size", FormatSize(udt.Size));
        }
        Property("TypeIndex", $"0x{typeIndex:X}");
        Property("Members", udt.MemberCount.ToString());

        string attributes = DescribeFlags(
            udt.Properties,
            (0x1, "packed"),
            (0x2, "has constructors/destructors"),
            (0x4, "overloaded operators"),
            (0x8, "nested type"),
            (0x10, "contains nested types"),
            (0x20, "operator="),
            (0x40, "conversion operators"),
            (0x100, "local scope"),
            (0x400, "sealed"),
            (0x2000, "intrinsic"));
        if (attributes.Length > 0)
        {
            Property("Attributes", attributes);
        }
        if (udt.UniqueName.Length > 0)
        {
            Property("Decorated name", Utf8(udt.UniqueName));
        }
        if (udt.IsForwardReference)
        {
            Property("Note", Lang.Tr("forward declaration only: no definition in this PDB"));
        }
        if (_pdb.FindSource(typeIndex) is { } source)
        {
            Property("Defined in", $"{source.File}({source.Line})");
        }
        if (_propertiesOnly)
        {
            return;
        }

        WriteHeader();
        var members = _pdb.GetMembers(udt.FieldList);
        if (isEnum)
        {
            WriteEnum(name, udt.UnderlyingType, members);
        }
        else
        {
            WriteClass(udt.Kind, name, members);
        }
    }

    /// <summary>A type that is not a class, struct, union or enum (reached through a typedef, for instance).</summary>
    void RenderOtherType(uint typeIndex, ReadOnlySpan<byte> record)
    {
        Property("Type", _document.Title);
        Property("TypeIndex", $"0x{typeIndex:X}");
        Property("Size", FormatSize(_pdb.SizeOf(typeIndex)));
        if (!record.IsEmpty)
        {
            Property("Record", ((LeafKind)U16(record, 0)).DisplayName());
        }
        if (_propertiesOnly)
        {
            return;
        }

        WriteHeader();
        WriteType(typeIndex).Write(";").Line();
    }

    void WriteEnum(string name, uint underlyingType, List<Member> members)
    {
        _out.Keyword("enum").Write(" ").Write(name, TokenKind.Type).Write(" : ");
        WriteType(underlyingType).Line().Write("{").Line();

        foreach (var enumerator in members)
        {
            if (enumerator.Kind != LF_ENUMERATE)
            {
                continue;
            }

            long value = enumerator.Offset;
            _out.Write(Indent).Write(enumerator.Name, TokenKind.Enumerator).Write(" = ").Number(value.ToString()).Write(",");
            if (value is > 9 or < 0)
            {
                _out.Comment($"   // 0x{value:X}");
            }
            _out.Line();
        }
        _out.Write("};").Line();
    }

    void WriteClass(LeafKind kind, string name, List<Member> members)
    {
        _out.Keyword(kind.Keyword()).Write(" ").Write(name, TokenKind.Type);
        WriteBaseClasses(members);
        _out.Line().Write("{").Line();

        _currentAccess = kind == LF_CLASS ? Member.Private : Member.Public;
        WriteNestedTypes(members);
        WriteFields(members);
        WriteStaticMembers(members);
        WriteMethods(name, members);

        foreach (var member in members)
        {
            if (member.Kind == Member.Undecoded)
            {
                _out.Write(Indent).Comment("// " + member.Name).Line();
            }
        }
        _out.TrimEmptyLine();
        _out.Write("};").Line();
    }

    void WriteBaseClasses(List<Member> members)
    {
        bool first = true;
        foreach (var baseClass in members)
        {
            if (baseClass.Kind is not (LF_BCLASS or LF_VBCLASS or LF_IVBCLASS))
            {
                continue;
            }

            _out.Line().Write(first ? "    : " : "    , ");
            first = false;

            bool isVirtual = baseClass.Kind != LF_BCLASS;
            if (isVirtual)
            {
                _out.Keyword("virtual ");
            }
            _out.Keyword(AccessKeyword(baseClass.Access)).Write(" ");
            WriteType(baseClass.Type);

            string location = isVirtual
                ? Lang.Format("virtual base, vbptr +0x{0:X}, vbtable index {1}", baseClass.Offset, baseClass.Extra)
                : Lang.Format("base at +0x{0:X}", baseClass.Offset);
            _out.Comment("    // " + location);
        }
    }

    void BeginSection(string title, int count)
    {
        _out.Write(Indent);
        WriteSectionTitle($"{Lang.Tr(title)} ({count})");
        _sectionIsEmpty = true;
    }

    /// <summary>
    /// Writes "public:" / "protected:" / "private:" when the access changes. A group of members ends
    /// with an empty line, and the label sits directly above the members it applies to.
    /// </summary>
    void WriteAccessLabel(Member member)
    {
        int access = member.Access;
        if (access != 0 && access != _currentAccess)
        {
            _currentAccess = access;
            if (!_sectionIsEmpty)
            {
                _out.Line();
            }
            _out.Keyword(AccessKeyword(access) + ":").Line();
        }
        _sectionIsEmpty = false;
    }

    void WriteNestedTypes(List<Member> members)
    {
        var nestedTypes = members.FindAll(member => member.Kind == LF_NESTTYPE);
        if (nestedTypes.Count == 0)
        {
            return;
        }

        BeginSection("Nested types", nestedTypes.Count);
        foreach (var nested in nestedTypes)
        {
            _out.Write(Indent);

            // The same record describes nested classes and typedefs. A nested class is named Outer::Name.
            bool isNestedDefinition = _pdb.TypeName(nested.Type).EndsWith("::" + nested.Name);
            if (isNestedDefinition)
            {
                _out.Keyword(_pdb.UdtKeyword(nested.Type)).Write(" ")
                    .Write(nested.Name, TokenKind.Type, new Target(ItemKind.Type, nested.Type)).Write(";");
            }
            else
            {
                _out.Keyword("typedef ");
                WriteDeclaration(nested.Type, nested.Name, TokenKind.Type).Write(";");
            }
            _out.Line();
        }
        _out.Line();
    }

    void WriteFields(List<Member> members)
    {
        const int typeColumnLimit = 32;
        const int nameColumnLimit = 36;

        var fields = members.FindAll(member => member.Kind is LF_MEMBER or LF_VFUNCTAB);
        if (fields.Count == 0)
        {
            return;
        }

        static bool IsVfptr(Member field) => field.Kind == LF_VFUNCTAB;
        static string NameOf(Member field) => IsVfptr(field) ? "__vfptr" : field.Name;

        BeginSection("Fields", fields.Count);
        var types = fields.ConvertAll(field => IsVfptr(field) ? new TypeParts("void**", "", false) : _pdb.SplitType(field.Type));
        int typeWidth = ColumnWidth(types.Select(type => type.NameIsEnclosed ? 0 : type.Prefix.Length), typeColumnLimit);
        int nameWidth = ColumnWidth(fields.Select((field, i) => NameOf(field).Length + types[i].Suffix.Length), nameColumnLimit);

        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var type = types[i];
            var bitfield = _pdb.GetBitfield(field.Type);
            string name = NameOf(field);

            WriteAccessLabel(field);
            _out.Write(Indent).Comment($"/* 0x{field.Offset:X4} */ ");
            WriteTypeText(type.Prefix, field.Type);
            if (!type.NameIsEnclosed)
            {
                _out.Spaces(Math.Max(1, typeWidth - type.Prefix.Length + 1));
            }

            string afterName = type.Suffix + (bitfield is { } bits ? $" : {bits.Length}" : "") + ";";
            _out.Write(name, TokenKind.Member).Write(afterName);
            _out.Spaces(Math.Max(1, nameWidth + 8 - name.Length - afterName.Length));

            if (bitfield is { } range)
            {
                _out.Comment("// " + Lang.Format("bit {0}, {1} bit(s)", range.Position, range.Length));
            }
            else
            {
                long size = IsVfptr(field) ? 8 : _pdb.SizeOf(field.Type);
                _out.Comment($"// 0x{size:X}");
            }
            _out.Line();
        }
        _out.Line();
    }

    void WriteStaticMembers(List<Member> members)
    {
        var statics = members.FindAll(member => member.Kind == LF_STMEMBER);
        if (statics.Count == 0)
        {
            return;
        }

        BeginSection("Static members", statics.Count);
        foreach (var member in statics)
        {
            WriteAccessLabel(member);
            _out.Write(Indent).Keyword("static ");
            WriteDeclaration(member.Type, member.Name, TokenKind.Member).Write(";").Line();
        }
        _out.Line();
    }

    void WriteMethods(string className, List<Member> members)
    {
        var methods = members.FindAll(member => member.Kind == LF_ONEMETHOD);
        if (methods.Count == 0)
        {
            return;
        }

        BeginSection("Methods", methods.Count);
        foreach (var method in methods)
        {
            WriteAccessLabel(method);
            string qualifiedName = className + "::" + method.Name;
            var kind = method.MethodKind;
            bool isPure = kind is MethodKind.PureVirtual or MethodKind.PureIntroducingVirtual;

            _out.Write(Indent);
            switch (kind)
            {
                case MethodKind.Static:
                    _out.Keyword("static ");
                    break;
                case MethodKind.Friend:
                    _out.Keyword("friend ");
                    break;
                case MethodKind.Virtual or MethodKind.IntroducingVirtual or MethodKind.PureVirtual or MethodKind.PureIntroducingVirtual:
                    _out.Keyword("virtual ");
                    break;
            }

            // The method's own symbol is not known from here: the name links to a search among the functions.
            var parameterNames = ParameterNames.OfMethod(_pdb, qualifiedName, method.Name, method.Type);
            WriteSignature(_pdb.GetSignature(method.Type), method.Name, parameterNames, search: qualifiedName);

            // A virtual method that does not add a vtable slot reuses the one of a base class.
            if (kind is MethodKind.Virtual or MethodKind.PureVirtual)
            {
                _out.Keyword(" override");
            }
            if (isPure)
            {
                _out.Write(" = ").Number("0");
            }
            _out.Write(";");

            string note = (method.Extra >= 0 ? $"vtable +0x{method.Extra:X}  " : "")
                + (method.IsCompilerGenerated ? Lang.Tr("compiler-generated") : "");
            if (note.Length > 0)
            {
                _out.Comment("   // " + note.TrimEnd());
            }
            _out.Line();
        }
    }
}
