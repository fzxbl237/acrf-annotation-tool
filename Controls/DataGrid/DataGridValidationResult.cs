using System;
using System.Collections;
using System.Collections.Generic;
using Avalonia.Data;

namespace PatChes.Controls.DataGrid;

public enum DataGridValidationSeverity
{
    None = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    InValid = 4
}

public sealed class DataGridValidationResult
{
    public DataGridValidationResult(string message, DataGridValidationSeverity severity = DataGridValidationSeverity.InValid)
    {
        Message = message;
        Severity = severity;
    }

    public string Message { get; }
    public DataGridValidationSeverity Severity { get; }
    public override string ToString() => Message;
}

internal static class ValidationUtil
{
    public static DataGridValidationSeverity GetValidationSeverity(IEnumerable<Exception> errors)
    {
        var severity = DataGridValidationSeverity.None;
        foreach (var error in errors)
        {
            var current = GetValidationSeverity(error);
            if (current > severity) severity = current;
        }
        return severity;
    }

    public static DataGridValidationSeverity GetValidationSeverity(Exception error)
    {
        if (error is DataValidationException dve && dve.ErrorData != null)
            return GetSeverityFromErrorData(dve.ErrorData);
        return DataGridValidationSeverity.InValid;
    }

    private static DataGridValidationSeverity GetSeverityFromErrorData(object errorData)
    {
        if (errorData is DataGridValidationResult result)
            return result.Severity;

        if (errorData is IEnumerable<DataGridValidationResult> results)
        {
            var severity = DataGridValidationSeverity.None;
            foreach (var item in results)
                if (item.Severity > severity) severity = item.Severity;
            return severity;
        }

        if (errorData is string)
            return DataGridValidationSeverity.InValid;

        if (errorData is IEnumerable enumerable)
        {
            var severity = DataGridValidationSeverity.None;
            foreach (var item in enumerable)
                if (item is DataGridValidationResult r && r.Severity > severity)
                    severity = r.Severity;
            return severity == DataGridValidationSeverity.None
                ? DataGridValidationSeverity.InValid
                : severity;
        }

        return DataGridValidationSeverity.InValid;
    }

    public static List<Exception> CreateValidationExceptions(IEnumerable errors)
    {
        var exceptions = new List<Exception>();
        foreach (var error in errors)
        {
            if (error == null) continue;
            if (error is Exception ex)
                exceptions.Add(ex);
            else
                exceptions.Add(new DataValidationException(error));
        }
        return exceptions;
    }
}
