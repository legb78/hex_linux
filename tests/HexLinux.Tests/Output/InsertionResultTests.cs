using HexLinux.Output;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// What the daemon learns from an insertion: the status and, when it did not
/// fully work, a reason for the notification. Never the text — the result
/// travels to the log and the tray, which must not see what was dictated.
/// </summary>
public class InsertionResultTests
{
    [Fact]
    public void A_successful_insertion_carries_no_problem()
    {
        var result = new InsertionResult(InsertionStatus.Inserted);

        Assert.Equal(InsertionStatus.Inserted, result.Status);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void A_copy_only_result_keeps_the_reason_for_the_notification()
    {
        // clipboardFallback: the user is told to press Ctrl+V, and why.
        var result = new InsertionResult(InsertionStatus.CopiedOnly, "nothing can send the paste shortcut");

        Assert.Equal(InsertionStatus.CopiedOnly, result.Status);
        Assert.Equal("nothing can send the paste shortcut", result.Problem);
    }

    [Fact]
    public void Results_compare_by_value()
    {
        Assert.Equal(new InsertionResult(InsertionStatus.Failed, "why"), new InsertionResult(InsertionStatus.Failed, "why"));
        Assert.NotEqual(new InsertionResult(InsertionStatus.Failed, "why"), new InsertionResult(InsertionStatus.Failed));
    }
}
