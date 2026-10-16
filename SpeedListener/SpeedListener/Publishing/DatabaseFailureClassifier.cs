using System.Data.Common;

namespace SpeedListener.Publishing;

/// <summary>Classifies provider failures by provider error code rather than wrapper exception type.</summary>
public static class DatabaseFailureClassifier
{
    /// <summary>Classifies a database or wrapper exception by its provider-specific error.</summary>
    public static DatabaseFailureKind Classify(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var kinds = aggregate.Flatten().InnerExceptions.Select(Classify).ToArray();
            if (kinds.Contains(DatabaseFailureKind.Fatal)) return DatabaseFailureKind.Fatal;
            if (kinds.Contains(DatabaseFailureKind.BatchData)) return DatabaseFailureKind.BatchData;
            return kinds.All(kind => kind == DatabaseFailureKind.Transient)
                ? DatabaseFailureKind.Transient
                : DatabaseFailureKind.Fatal;
        }

        if (exception is TimeoutException) return DatabaseFailureKind.Transient;
        if (exception is OperationCanceledException) return DatabaseFailureKind.Fatal;
        if (exception is DbException databaseException) return ClassifyProviderError(databaseException);
        return exception.InnerException is not null ? Classify(exception.InnerException) : DatabaseFailureKind.Fatal;
    }

    private static DatabaseFailureKind ClassifyProviderError(DbException exception)
    {
        var sqlState = exception.GetType().GetProperty("SqlState")?.GetValue(exception) as string;
        if (!string.IsNullOrEmpty(sqlState))
        {
            if (sqlState.StartsWith("22", StringComparison.Ordinal) || sqlState.StartsWith("23", StringComparison.Ordinal))
                return DatabaseFailureKind.BatchData;
            if (sqlState.StartsWith("08", StringComparison.Ordinal) || sqlState.StartsWith("53", StringComparison.Ordinal) ||
                sqlState is "40001" or "40P01" or "55P03" or "57014" or "57P01" or "57P02" or "57P03")
                return DatabaseFailureKind.Transient;
            return DatabaseFailureKind.Fatal;
        }

        var numberValue = exception.GetType().GetProperty("Number")?.GetValue(exception);
        if (numberValue is int number)
        {
            if (number is 2601 or 2627 or 547 or 515 or 8115 or 8152 or 2628)
                return DatabaseFailureKind.BatchData;
            if (number is -2 or 1205 or 233 or 64 or 4060 or 10928 or 10929 or 40197 or 40501 or 40613 or
                49918 or 49919 or 49920 or 10053 or 10054 or 10060)
                return DatabaseFailureKind.Transient;
            return DatabaseFailureKind.Fatal;
        }

        var sqliteCode = exception.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception);
        if (sqliteCode is int code)
        {
            if (code == 19) return DatabaseFailureKind.BatchData;
            if (code is 5 or 6) return DatabaseFailureKind.Transient;
            return DatabaseFailureKind.Fatal;
        }

        return exception.IsTransient ? DatabaseFailureKind.Transient : DatabaseFailureKind.Fatal;
    }
}

/// <summary>Describes the operational response required for a database failure.</summary>
public enum DatabaseFailureKind
{
    /// <summary>The operation may succeed when retried.</summary>
    Transient,
    /// <summary>The rejected data is attributable to a particular batch.</summary>
    BatchData,
    /// <summary>The failure is systemic and must stop the service.</summary>
    Fatal
}
