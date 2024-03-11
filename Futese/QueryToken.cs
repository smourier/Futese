namespace Futese
{
    public class QueryToken(string text, QueryTokenType type) : Token(text)
    {
        public virtual QueryTokenType Type { get; set; } = type;

        public override string ToString() => Type + ":" + base.ToString();
    }
}
