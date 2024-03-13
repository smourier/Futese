using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Futese
{
    public class DefaultTokenizer : ITokenizer
    {
        public virtual IEnumerable<Token> EnumerateTokens(string? text)
        {
            text = Preprocess(text)!;
            if (string.IsNullOrEmpty(text))
                yield break;

            var start = 0;
            var len = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (BreaksToken(c))
                {
                    if (len > 0)
                    {
                        var token = BuildToken(text.Substring(start, len));
                        if (token != null)
                            yield return token;
                    }

                    start = i + 1;
                    len = 0;
                    continue;
                }
                len++;
            }

            if (len > 0)
            {
                var token = BuildToken(text.Substring(start, len));
                if (token != null)
                    yield return token;
            }
        }

        protected virtual bool BreaksToken(char c) => !char.IsAsciiLetter(c);
        protected virtual Token? BuildToken(string? text) => !string.IsNullOrEmpty(text) ? new(text) : null;
        protected virtual string? Preprocess(string? text) => RemoveDiacritics(text);

        [return: NotNullIfNotNull(nameof(text))]
        public static string? RemoveDiacritics(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            for (var i = 0; i < normalized.Length; i++)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(normalized[i]);
                if (uc != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(char.ToLowerInvariant(normalized[i]));
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
