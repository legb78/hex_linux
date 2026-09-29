using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

public class NotificationRulesTests
{
    [Fact]
    public void A_server_that_reads_markup_gets_the_special_characters_escaped()
    {
        // Otherwise a path with an ampersand is cut short, or the whole body
        // dropped as invalid markup.
        Assert.Equal("/home/a&amp;b &lt;x&gt;", NotificationRules.Body("/home/a&b <x>", serverReadsMarkup: true));
    }

    [Fact]
    public void A_server_without_markup_gets_the_text_as_it_is()
    {
        // It would print "&amp;" literally.
        Assert.Equal("/home/a&b <x>", NotificationRules.Body("/home/a&b <x>", serverReadsMarkup: false));
    }

    [Fact]
    public void Notify_send_receives_the_title_after_the_end_of_options()
    {
        // A title that begins with a dash must not be read as an option.
        IReadOnlyList<string> arguments = NotificationRules.NotifySendArguments("-x", "body");

        Assert.Equal(["-a", "HexLinux", "-i", "audio-input-microphone", "-u", "normal", "--", "-x", "body"], arguments);
    }

    [Fact]
    public void Notify_send_receives_title_and_body_as_two_whole_arguments()
    {
        // No shell reads them: quotes, spaces and dollars reach it unchanged.
        IReadOnlyList<string> arguments = NotificationRules.NotifySendArguments("Can't open", "/tmp/a b/$HOME \"x\"");

        Assert.Equal("Can't open", arguments[^2]);
        Assert.Equal("/tmp/a b/$HOME \"x\"", arguments[^1]);
    }

    // --- Replacing a repeated notification ---------------------------------------------

    [Fact]
    public void A_first_notification_replaces_nothing()
    {
        Assert.Equal(0u, new NotificationLedger().ReplacesIdFor("Session locked"));
    }

    [Fact]
    public void A_repeated_failure_replaces_its_previous_notification()
    {
        // A locked session refuses every dictation: one notification updated
        // in place, not a pile of identical ones.
        var ledger = new NotificationLedger();
        ledger.Remember("Session locked", 7);

        Assert.Equal(7u, ledger.ReplacesIdFor("Session locked"));
    }

    [Fact]
    public void Different_failures_keep_their_own_notifications()
    {
        var ledger = new NotificationLedger();
        ledger.Remember("Session locked", 7);
        ledger.Remember("Microphone unavailable", 9);

        Assert.Equal(7u, ledger.ReplacesIdFor("Session locked"));
        Assert.Equal(9u, ledger.ReplacesIdFor("Microphone unavailable"));
    }

    [Fact]
    public void A_zero_id_from_the_server_is_forgotten()
    {
        // The specification says servers never return 0; one that does must not
        // make the next notification "replace" nothing in particular.
        var ledger = new NotificationLedger();
        ledger.Remember("Session locked", 7);
        ledger.Remember("Session locked", 0);

        Assert.Equal(0u, ledger.ReplacesIdFor("Session locked"));
    }

    [Fact]
    public void The_ledger_stays_small_whatever_the_titles()
    {
        // Titles are constants; a caller that built them on the fly must not
        // grow the ledger for ever.
        var ledger = new NotificationLedger();
        ledger.Remember("first", 1);

        for (uint id = 2; id <= 40; id++)
        {
            ledger.Remember($"title {id}", id);
        }

        Assert.Equal(0u, ledger.ReplacesIdFor("first"));
        Assert.Equal(40u, ledger.ReplacesIdFor("title 40"));
    }
}
