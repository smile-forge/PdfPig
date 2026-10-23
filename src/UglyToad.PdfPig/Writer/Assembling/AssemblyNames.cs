namespace UglyToad.PdfPig.Writer.Assembling;

using Tokens;

/// <summary>
/// The names an assembly reads and writes that <see cref="NameToken"/> holds no constant for.
/// </summary>
internal static class AssemblyNames
{
    public static readonly NameToken AppearanceState = NameToken.Create("AS");

    public static readonly NameToken AssociatedFiles = NameToken.Create("AF");

    public static readonly NameToken ClassMap = NameToken.Create("ClassMap");

    public static readonly NameToken FormType = NameToken.Create("FormType");

    public static readonly NameToken InkList = NameToken.Create("InkList");

    public static readonly NameToken Configs = NameToken.Create("Configs");

    public static readonly NameToken InReplyTo = NameToken.Create("IRT");

    public static readonly NameToken Locked = NameToken.Create("Locked");

    public static readonly NameToken MarkedContentReference = NameToken.Create("MCR");

    public static readonly NameToken ModificationDate = NameToken.Create("ModDate");

    public static readonly NameToken Namespaces = NameToken.Create("Namespaces");

    public static readonly NameToken ObjectReference = NameToken.Create("OBJR");

    public static readonly NameToken PageLabel = NameToken.Create("PageLabel");

    public static readonly NameToken Popup = NameToken.Create("Popup");

    public static readonly NameToken PronunciationLexicon = NameToken.Create("PronunciationLexicon");

    public static readonly NameToken QuadPoints = NameToken.Create("QuadPoints");

    public static readonly NameToken RadioButtonGroups = NameToken.Create("RBGroups");

    public static readonly NameToken StructureElement = NameToken.Create("StructElem");

    public static readonly NameToken Stm = NameToken.Create("Stm");

    public static readonly NameToken Suspects = NameToken.Create("Suspects");

    public static readonly NameToken Tabs = NameToken.Create("Tabs");

    public static readonly NameToken UserProperties = NameToken.Create("UserProperties");
}
