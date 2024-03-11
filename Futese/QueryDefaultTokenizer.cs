namespace Futese
{
    public class QueryDefaultTokenizer : DefaultTokenizer
    {
        private QueryTokenType _nextTokenType;

        private static QueryTokenType? GetNextToken(char c) => c switch
        {
            '-' => QueryTokenType.Not,
            '|' => QueryTokenType.Or,
            '+' => QueryTokenType.And,
            _ => null,
        };

        protected override Token? BuildToken(string? text)
        {
            Token? token;
            if (text?.Length > 0)
            {
                var next = GetNextToken(text[0]);
                if (next != null)
                {
                    if (text.Length == 1)
                    {
                        _nextTokenType = next.Value;
                        return null;
                    }

                    token = base.BuildToken(text[1..]);
                }
                else
                {
                    token = base.BuildToken(text!);
                }
            }
            else
            {
                token = base.BuildToken(text!);
            }

            if (token != null)
            {
                if (_nextTokenType != QueryTokenType.And)
                {
                    token = new QueryToken(token.Text, _nextTokenType);
                    _nextTokenType = QueryTokenType.And;
                }
            }
            return token;
        }

        protected override bool BreaksToken(char c)
        {
            var next = GetNextToken(c);
            if (next != null)
            {
                _nextTokenType = next.Value;
            }
            return base.BreaksToken(c);
        }
    }
}
