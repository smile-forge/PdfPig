namespace UglyToad.PdfPig.Annotations
{
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using Tokenization.Scanner;
    using Tokens;

    internal static class AppearanceStreamFactory
    {
        public static bool TryCreate(DictionaryToken appearanceDictionary, NameToken name, IPdfTokenScanner tokenScanner, [NotNullWhen(true)] out AppearanceStream? appearanceStream)
        {
            appearanceStream = null;
            if (!appearanceDictionary.TryGet(name, out var entry))
            {
                return false;
            }

            // An appearance is a stream, or a dictionary of streams by appearance state (12.5.5); a dictionary of states may be
            // written in place or by reference, as the stream always is.
            if (entry is IndirectReferenceToken appearanceReference)
            {
                var target = tokenScanner.Get(appearanceReference.Data)?.Data;
                if (target is not DictionaryToken states)
                {
                    appearanceStream = new AppearanceStream(target as StreamToken);
                    return true;
                }

                entry = states;
            }

            if (entry is DictionaryToken stateDictionary)
            {
                var dict = new Dictionary<string, StreamToken>();
                foreach (var state in stateDictionary.Data.Keys)
                {
                    if (stateDictionary.Data.TryGetValue(state, out var stateRef) &&
                        stateRef is IndirectReferenceToken appearanceRef)
                    {
                        var streamToken = tokenScanner.Get(appearanceRef.Data)?.Data as StreamToken;
                        dict[state] = streamToken!;
                    }
                }

                if (dict.Count > 0)
                {
                    appearanceStream = new AppearanceStream(dict);
                    return true;
                }
            }

            return false;
        }
    }
}
