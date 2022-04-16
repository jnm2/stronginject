using Microsoft.CodeAnalysis.Text;
using System;
using System.Diagnostics;
using System.Text;

namespace StrongInject.Generator;

internal class AutoIndenter
{
    public int Indent => _indent;
    
    public AutoIndenter(int initialIndent)
    {
        _indent = initialIndent;
        BeginLine();
    }
    
    private readonly SourceTextBuilder _text = new();
    private int _indent;
    const string INDENT = "    ";
    public void Append(string str)
    {
        _text.Append(str);
    }

    public void Append(char c)
    {
        _text.Append(c);
    }
    
    public void AppendIndented(string str)
    {
        _text.Append(INDENT);
        _text.Append(str);
    }

    public void AppendLine(char c)
    {
        switch (c)
        {
            case '}':
                _indent--;
                var lastChar = _text[_text.Length - 1];
                _text.RemoveLast(5);
                _text.Append(lastChar);
                break;
            case '{':
                _indent++;
                break;
        }
        _text.Append(c);
        _text.AppendLine();
        BeginLine();
    }
    
    public void AppendLineIndented(string str)
    {
        _text.Append(INDENT);
        _text.AppendLine(str);
        BeginLine();
    }
    
    public void AppendLine(string str)
    {
        switch (str[0])
        {
            case '}':
                _indent--;
                _text.RemoveLast(4);
                break;
            case '{':
                _indent++;
                break;
        }
        _text.AppendLine(str);
        BeginLine();
    }
    
    public void AppendLine()
    {
        _text.RemoveLast(_indent * 4);
        _text.AppendLine();
        BeginLine();
    }

    private void BeginLine()
    {
        for (int i = 0; i < _indent; i++)
        {
            _text.Append(INDENT);
        }
    }

    public SourceText Build() => _text.Build();

    public AutoIndenter GetSubIndenter()
    {
        return new AutoIndenter(_indent);
    }

    public void Append(AutoIndenter subIndenter)
    {
        Debug.Assert(subIndenter._indent == _indent);
        _text.RemoveLast(_indent * 4);
        _text.Append(subIndenter._text);
    }
}