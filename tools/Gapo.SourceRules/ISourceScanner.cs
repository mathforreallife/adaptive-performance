namespace Gapo.SourceRules;

public interface ISourceScanner
{
    bool Handles(string path);

    IEnumerable<Finding> Scan(string path, string text);
}
