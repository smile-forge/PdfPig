namespace UglyToad.PdfPig.Tests.ContentTests
{
    using System.Collections.Generic;
    using PdfPig.Content;
    using PdfPig.Core;
    using PdfPig.PdfFonts;
    using PdfPig.Tokens;
    using PdfPig.Tests.Tokens;
    using Xunit;

    /// <summary>
    /// A named property list is known by the object it is: two optional content groups may have equal dictionaries and
    /// still be shown and hidden apart (8.11.2), so the dictionary alone cannot tell them apart.
    /// </summary>
    public class ResourceStoreMarkedContentReferenceTests
    {
        private static readonly IndirectReference First = new IndirectReference(386, 0);
        private static readonly IndirectReference Second = new IndirectReference(396, 0);

        private sealed class NoOpFontFactory : IFontFactory
        {
            public IFont Get(DictionaryToken dictionary) => null!;
        }

        [Fact]
        public void GroupsWithEqualDictionaries_AreEachTheirOwnObject()
        {
            var scanner = Scanner();
            var store = Store(scanner);

            store.LoadResourceDictionary(Resources(
                ("Pr1", new IndirectReferenceToken(First)),
                ("Pr2", new IndirectReferenceToken(Second))));

            Assert.True(store.TryGetMarkedContentPropertiesReference(NameToken.Create("Pr1"), out var first));
            Assert.True(store.TryGetMarkedContentPropertiesReference(NameToken.Create("Pr2"), out var second));
            Assert.Equal(First, first);
            Assert.Equal(Second, second);
            Assert.Equal(store.GetMarkedContentPropertiesDictionary(NameToken.Create("Pr1")),
                store.GetMarkedContentPropertiesDictionary(NameToken.Create("Pr2")));
        }

        [Fact]
        public void AListGivenInline_IsNoObject_AndHidesOneOfItsNameBeneathIt()
        {
            var scanner = Scanner();
            var store = Store(scanner);

            store.LoadResourceDictionary(Resources(("Pr1", new IndirectReferenceToken(First))));
            store.LoadResourceDictionary(Resources(("Pr1", Group("inline"))));

            Assert.False(store.TryGetMarkedContentPropertiesReference(NameToken.Create("Pr1"), out _));

            store.UnloadResourceDictionary();

            Assert.True(store.TryGetMarkedContentPropertiesReference(NameToken.Create("Pr1"), out var outer));
            Assert.Equal(First, outer);
        }

        [Fact]
        public void ANameTheResourcesLack_IsNoObject()
        {
            var store = Store(Scanner());
            store.LoadResourceDictionary(Resources());

            Assert.False(store.TryGetMarkedContentPropertiesReference(NameToken.Create("Pr1"), out _));
        }

        private static TestPdfTokenScanner Scanner()
        {
            var scanner = new TestPdfTokenScanner();
            foreach (var reference in new[] { First, Second })
            {
                scanner.Objects[reference] = new ObjectToken(XrefLocation.File(0), reference, Group("guia numero pag impar"));
            }

            return scanner;
        }

        private static DictionaryToken Group(string name) => new DictionaryToken(new Dictionary<NameToken, IToken>
        {
            { NameToken.Type, NameToken.Create("OCG") },
            { NameToken.Name, new StringToken(name) }
        });

        private static DictionaryToken Resources(params (string Name, IToken Value)[] properties)
        {
            var list = new Dictionary<NameToken, IToken>();
            foreach (var (name, value) in properties)
            {
                list[NameToken.Create(name)] = value;
            }

            return new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Properties, new DictionaryToken(list) }
            });
        }

        private static ResourceStore Store(TestPdfTokenScanner scanner) => new ResourceStore(
            scanner,
            new NoOpFontFactory(),
            new TestFilterProvider(),
            null,
            new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
    }
}
