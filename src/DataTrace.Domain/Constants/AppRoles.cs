namespace DataTrace.Domain.Constants;

public static class AppRoles
{
    public const string Administrator = "Administrator";
    public const string Engineer = "Engineer";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Administrator, Engineer, Operator, Viewer];
}
