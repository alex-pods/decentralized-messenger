using MessengerDesktop.Settings;

namespace MessengerDesktop;

public static class PreferencesCheck
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "messenger-preferences-" + Guid.NewGuid().ToString("N"));
        var oldPath = AppPreferences.FilePath;
        var oldPins = LocalPinStore.DirectoryPath;
        Directory.CreateDirectory(directory);
        try
        {
            AppPreferences.FilePath = Path.Combine(directory, "settings.json");
            AppPreferences.Load();
            var expected = new UserPreferences { SendMode = SendMode.Enter, DoubleClick = DoubleClickAction.React, DoubleClickReaction = "🔥" };
            AppPreferences.Update(_ => expected);
            AppPreferences.Load();
            Require(AppPreferences.Current == expected && AppPreferences.SaveError == null, "Settings did not survive reload");
            File.WriteAllText(AppPreferences.FilePath, "{\"SendMode\":999,\"DoubleClick\":-1,\"DoubleClickReaction\":\"unknown\"}");
            AppPreferences.Load();
            Require(AppPreferences.Current == new UserPreferences(), "Invalid preference values were accepted");
            File.WriteAllText(AppPreferences.FilePath, "broken");
            AppPreferences.Load();
            Require(AppPreferences.Current == new UserPreferences(), "Corrupt preferences did not fall back safely");
            File.Delete(AppPreferences.FilePath);
            Directory.CreateDirectory(AppPreferences.FilePath);
            AppPreferences.Update(_ => expected);
            Require(AppPreferences.SaveError != null && AppPreferences.Current == expected, "Save failure is hidden or lost the active preferences");
            Directory.Delete(AppPreferences.FilePath);
            AppPreferences.Update(_ => expected);
            Require(AppPreferences.SaveError == null, "Save error was not cleared after recovery");

            LocalPinStore.DirectoryPath = Path.Combine(directory, "pins");
            LocalPinStore.Save("https://one.example/api/dev", "u1", [new("c2", "m3", "local pin")]);
            Require(LocalPinStore.Load("https://one.example/api/dev/", "u1").Single().Text == "local pin", "Local pins did not persist");
            Require(LocalPinStore.Load("https://one.example/api/dev", "u2").Count == 0 &&
                LocalPinStore.Load("https://two.example/api/dev", "u1").Count == 0, "Pins leaked across users or servers");
            Console.WriteLine("PASS preferences: persistence, validation, corruption, write failure/recovery; local pin account/server isolation");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
        finally
        {
            AppPreferences.FilePath = oldPath;
            LocalPinStore.DirectoryPath = oldPins;
            Directory.Delete(directory, true);
        }
    }

    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
