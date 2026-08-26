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
                int start = _position;
                while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
                    _position++;
                string word = _source[start.._position];
                tokens.Add(new Token(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Ident, word));
            }
            else if (char.IsDigit(c) || (c == '-' && StartsNegativeNumber(tokens)))
            {
                int start = _position;
                if (_source[_position] == '-') _position++