namespace Vagabond.Common.Data;

public static class Messages
{
    public static string WelcomeOpenWorld()
    {
        return
            "Welcome, Vagabond.\n\n" +
            "Your start out with some money and access to Fence. Once you deploy you you won't easily be able to extract.\n" +
            "You will need to worry about food, water and ammunition more than usual, as you will likely stay in raid for much longer.\n\n" +
            "== Your Hideout ==\n\n" +
            "You can place your hideout entrance anywhere in the game, theoretically. Go to where you want your hideout entrance to be and press CTRL+P.\n" +
            "Important: You cannot move your hideout once it is placed (for now) so decide carefully.\n\n" +
            "== Stashes ==\n\n" +
            "Every trader has their own stash separate from your hideout stash. If you use another player's hideout exfil, that also will have its own stash.\n\n" +
            "Good Luck!";
    }

    public static string OnDeathHideoutFailed()
    {
        return
            "You died, but you have not placed a hideout yet.\n\n" +
            "Your 'OnDeathGoTo' config is set to 'hideout', but with no hideout to send you to, you stayed where you fell.\n" +
            "Press CTRL+P (rebind via F12) in a raid to place your hideout entrance.";
    }

    public static string OnDeathCustomFailed(string raid, string exfil)
    {
        return
            "You died, but the configured custom respawn could not be resolved.\n\n" +
            $"Tried raid '{raid}' exfil '{exfil}'. Either the raid name is not valid, or the exfil identifier is not present in that raid's exfils config.\n" +
            "You stayed where you fell. Check your 'vagabond.json' 'OnDeathGoToRaid' and 'OnDeathGoToExfilIdentifier' values, then restart the server.";
    }

    public static string OnDeathInvalid(string value)
    {
        return
            $"You died, but your 'OnDeathGoTo' value '{value}' is not valid.\n\n" +
            "Accepted values: 'hideout', 'stay', 'custom'. You stayed where you fell. Fix the value in 'vagabond.json' and restart the server.";
    }

    public static string FirstWarning(bool wipeFirstRaid, bool permadeath, bool virtualStashes)
    {
        var message = "Welcome, Vagabond.\n\n" +
                      "You start out with a bit of money, maybe just enough to buy a ragtag setup from Fence.\n" +
                      "While in a raid, you can press CTRL+P (change via F12) to place the entrance to your hideout to get access to your stash. However once placed you have to pay Skier before you can move it.\n" +
                      "To get traders to be accessible via your hideout, you will need to complete a unique quest for each one. These quests will be available once you reach a certain rep level.\n";

        if (virtualStashes)
        {
            message +=
                "\nAll stashes, except your hideout stash, is unique to that trader and/or extraction. Some features are disabled in these stashes like sorting.\n";
        }

        if (permadeath)
        {
            message +=
                "\nPerma-Death is enabled. If you die or the game treats you as dead, your equipment and all stashes are wiped and you are reset back to the starter map.\n";
        }

        if (wipeFirstRaid)
        {
            message += $"\nFirst time you enter a raid anything left in this stash will get wiped.";
        }

        return message;
    }
}