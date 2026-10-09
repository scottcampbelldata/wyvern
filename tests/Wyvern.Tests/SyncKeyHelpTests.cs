using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>The words Wyvern shows about the SSH key file it makes for sync.</summary>
public class SyncKeyHelpTests
{
    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMadeUpMadeUpMadeUpMadeUpMadeUpMadeUpMadeUpMadeUp wyvern@OTTER-PC";

    [Fact]
    public void The_key_file_is_in_the_ssh_folder_of_the_profile_under_wyvern_s_own_name()
    {
        Assert.Equal(@"C:\Users\made-up\.ssh\wyvern_ed25519", SyncKeyHelp.KeyFile(@"C:\Users\made-up"));
    }

    [Theory]
    [InlineData(PublicKey, true)]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMadeUpMadeUpMadeUpMadeUpMadeUpMadeUpMadeUpMadeUp", true)]
    [InlineData("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQDmadeupmadeupmadeupmadeup comment with spaces", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("ssh-ed25519", false)]
    [InlineData("ssh-ed25519 short", false)]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", false)]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMadeUpMadeUpMadeUpMadeUpMadeUpMadeUpMadeUpMadeUp\nsecond line", false)]
    public void A_public_half_is_one_line_with_its_kind_and_its_text(string? line, bool isOne) =>
        Assert.Equal(isOne, SyncKeyHelp.IsPublicKey(line));

    [Fact]
    public void A_line_that_is_far_too_long_is_not_taken_for_a_key()
    {
        Assert.False(SyncKeyHelp.IsPublicKey("ssh-ed25519 " + new string('A', 3000)));
    }

    [Fact]
    public void The_steps_show_the_public_half_and_the_file_and_say_whether_the_key_is_new()
    {
        string made = SyncKeyHelp.Steps(PublicKey, @"C:\Users\made-up\.ssh\wyvern_ed25519", created: true);
        string found = SyncKeyHelp.Steps(PublicKey, @"C:\Users\made-up\.ssh\wyvern_ed25519", created: false);

        Assert.StartsWith("A new key was made for this PC.", made);
        Assert.StartsWith("This PC already has a key for Wyvern. It was not changed.", found);
        foreach (string steps in new[] { made, found })
        {
            // The public half stands on a line of its own, so that it can be selected whole.
            Assert.Contains("\n\n" + PublicKey + "\n\n", steps);
            Assert.Contains("\n\n" + @"C:\Users\made-up\.ssh\wyvern_ed25519" + "\n\n", steps);
            Assert.Contains("authorized_keys", steps);
            Assert.Contains("Sync Setup", steps);
            Assert.Contains("never sends it anywhere", steps);
        }
    }
}
