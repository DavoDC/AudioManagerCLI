using System;
using System.IO;

namespace AudioManager
{
    /// <summary>The integrate menu prompt must not loop forever when stdin is closed (82 MB of prompts seen in a dry run).</summary>
    internal static class MenuKeyEofTests
    {
        public static void ReadMenuKey_StdinEof_ReturnsQuitAndPromptsOnce()
        {
            var output = new StringWriter();
            ConsoleKey key = MusicIntegrator.ReadMenuKeyFrom(new StringReader(""), output);
            Assert.True(key == ConsoleKey.Q, "EOF must quit, got " + key);
            Assert.True(output.ToString().Length < 200, "bounded output, got " + output.ToString().Length + " chars");
        }

        public static void ReadMenuKey_InvalidThenValid_ReturnsValidKey()
        {
            ConsoleKey key = MusicIntegrator.ReadMenuKeyFrom(new StringReader("x\n\nl\n"), new StringWriter());
            Assert.True(key == ConsoleKey.L, "got " + key);
        }

        public static void ReadMenuKey_InvalidThenEof_ReturnsQuit()
        {
            ConsoleKey key = MusicIntegrator.ReadMenuKeyFrom(new StringReader("x\ny\n"), new StringWriter());
            Assert.True(key == ConsoleKey.Y || key == ConsoleKey.Q, "got " + key);
            key = MusicIntegrator.ReadMenuKeyFrom(new StringReader("x\nzz\n"), new StringWriter());
            Assert.True(key == ConsoleKey.Q, "invalid input then EOF must quit, got " + key);
        }
    }
}
