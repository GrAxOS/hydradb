using System.Text;

namespace HydraDB.Core.Sql;

public sealed class Lexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "create", "table", "primary", "key", "foreign", "references",
        "insert", "into", "values", "select", "from", "where",
        "update", "set", "delete", "limit", "order", "by", "asc", "desc",
        "and", "or", "not", "begin", "commit", "rollback",
        "int", "text", "bool", "true", "false", "null"
    };

    private const string SinglePunctuation = "(),*=<>;.";

    private readonly string _source;
    private int _position;

    public Lexer(string source) => _source = source;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (true)
        {
            SkipWhitespaceAndComments();

            if (_position >= _source.Length)
            {
                tokens.Add(new Token(TokenKind.Eof, string.Empty));
                return tokens;
            }

            char c = _source[_position];

            if (char.IsLetter(c) || c == '_')
            {
                tokens.Add(ReadWord());
            }
            else if (char.IsDigit(c) || (c == '-' && StartsNegativeNumber(tokens)))
            {
                tokens.Add(ReadNumber());
            }
            else if (c == '\'')
            {
                tokens.Add(ReadString());
            }
            else
            {
                tokens.Add(ReadPunctuation(c));
            }
        }
    }

    private Token ReadWord()
    {
        int start = _position;
        while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
            _position++;

        string word = _source[start.._position];
        return new Token(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Ident, word);
    }

    private Token ReadNumber()
    {
        int start = _position;
        if (_source[_position] == '-') _position++;
        while (_position < _source.Length && char.IsDigit(_source[_position])) _position++;
        return new Token(TokenKind.Number, _source[start.._position]);
    }

    private Token ReadString()
    {
        _position++;
        var builder = new StringBuilder();

        while (true)
        {
            if (_position >= _source.Length) throw new SqlException("unterminated string literal");

            char c = _source[_position++];
            if (c != '\'')
            {
                builder.Append(c);
                continue;
            }

            // '' inside a literal is an escaped quote.
            if (_position < _source.Length && _source[_position] == '\'')
            {
                builder.Append('\'');
                _position++;
                continue;
            }

            break;
        }

        return new Token(TokenKind.String, builder.ToString());
    }

    private Token ReadPunctuation(char c)
    {
        if (_position + 1 < _source.Length)
        {
            string pair = _source.Substring(_position, 2);
            if (pair is "<=" or ">=" or "<>")
            {
                _position += 2;
                return new Token(TokenKind.Punct, pair);
            }
            if (pair == "!=")
            {
                _position += 2;
                return new Token(TokenKind.Punct, "<>");
            }
        }

        if (SinglePunctuation.IndexOf(c) >= 0)
        {
            _position++;
            return new Token(TokenKind.Punct, c.ToString());
        }

        throw new SqlException($"unexpected character '{c}'");
    }

    /// <summary>A '-' starts a literal only where a value is expected, never where an operator would be.</summary>
    private static bool StartsNegativeNumber(List<Token> tokens)
    {
        if (tokens.Count == 0) return true;

        Token last = tokens[^1];
        if (last.Kind == TokenKind.Keyword) return true;
        return last.Kind == TokenKind.Punct
               && last.Text is "(" or "," or "=" or "<" or ">" or "<=" or ">=" or "<>";
    }

    private void SkipWhitespaceAndComments()
    {
        while (_position < _source.Length)
        {
            if (char.IsWhiteSpace(_source[_position]))
            {
                _position++;
                continue;
            }

            if (_position + 1 < _source.Length && _source[_position] == '-' && _source[_position + 1] == '-')
            {
                while (_position < _source.Length && _source[_position] != '\n') _position++;
                continue;
            }

            return;
        }
    }
}
