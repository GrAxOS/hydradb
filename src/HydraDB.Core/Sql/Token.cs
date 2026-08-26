namespace HydraDB.Core.Sql;

public enum TokenKind
{
    Ident,
    Number,
    String,
    Punct,
    Keyword,
    Eof
}

public readonly struct Token
{
    public Token(TokenKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public TokenKind Kind { get; }
    public string Text { get; }

    public bool Is(string text) => string.Equals(Text, text, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Kind}:{Text}";
}
