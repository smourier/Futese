using System.Collections.Generic;

namespace Futese
{
    public interface ITokenizer
    {
        IEnumerable<Token> EnumerateTokens(string text);
    }
}
