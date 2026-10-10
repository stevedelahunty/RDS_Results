namespace RDS_Results;

static class ConnectAddress
{
    public static string Host(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("::1", StringComparison.Ordinal) ||
            trimmed.Equals("[::1]", StringComparison.Ordinal))
            return "127.0.0.1";
        return trimmed;
    }
}
