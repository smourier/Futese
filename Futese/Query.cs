using System;
using System.Collections.Generic;
using System.Linq;

namespace Futese
{
    public class Query
    {
        public Query(string text, ITokenizer? tokenizer = null)
        {
            ArgumentNullException.ThrowIfNull(text);
            OriginalText = text;
            Tokens = (tokenizer ?? new QueryDefaultTokenizer()).EnumerateTokens(OriginalText).ToArray();
        }

        public string OriginalText { get; }
        public IReadOnlyList<Token> Tokens { get; } = [];

        public override string ToString() => OriginalText;
    }
}
