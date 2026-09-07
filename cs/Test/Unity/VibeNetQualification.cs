#if UNITY_5_3_OR_NEWER
using System;
using System.IO;
using UnityEngine;
public sealed class VibeNetQualification : MonoBehaviour
{
    private async void Start()
    {
        string path = Path.Combine(Application.dataPath, "../qualification-results.txt");
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-resultPath") path = args[i + 1];
        int code = 1;
        try { using (var output = new StreamWriter(path, false)) { output.AutoFlush = true; Console.SetOut(output); Console.WriteLine(Application.unityVersion + " / " + Application.platform); code = await Program.RunTests(); } }
        catch (Exception ex) { File.AppendAllText(path, ex.ToString()); }
        Application.Quit(code);
    }
}
#endif
