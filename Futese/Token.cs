namespace Futese
{
    public class Token(string text)
    {
        public virtual string Text { get; } = text;

        public override string ToString() => Text;
    }
}
