using UnityEngine;

namespace NeonStage.Stage
{
public static class StageLocale
{
    public static bool German => Application.systemLanguage == SystemLanguage.German;
    public static string Text(string german, string english) => German ? german : english;
}
}
