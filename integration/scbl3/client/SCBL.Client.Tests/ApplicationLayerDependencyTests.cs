using System.Reflection;
using SplinterCellCNLauncher.ApplicationLayer;

namespace SCBL.Client.Tests;

public sealed class ApplicationLayerDependencyTests
{
    [Fact]
    public void ApplicationLayerPublicSurfaceHasNoWpfDependencies()
    {
        Assembly assembly = typeof(LauncherInteractionPolicy).Assembly;
        Type[] applicationTypes = assembly.GetTypes()
            .Where(type => type.Namespace == "SplinterCellCNLauncher.ApplicationLayer")
            .ToArray();

        var violations = new List<string>();
        foreach (Type type in applicationTypes)
        {
            Check(type, "base", type.BaseType, violations);
            foreach (Type contract in type.GetInterfaces())
                Check(type, "interface", contract, violations);
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
                Check(type, $"field {field.Name}", field.FieldType, violations);
            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
                Check(type, $"property {property.Name}", property.PropertyType, violations);
            foreach (MethodInfo method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
            {
                Check(type, $"method {method.Name} return", method.ReturnType, violations);
                foreach (ParameterInfo parameter in method.GetParameters())
                    Check(type, $"method {method.Name} parameter {parameter.Name}", parameter.ParameterType, violations);
            }
        }

        Assert.True(
            violations.Count == 0,
            "ApplicationLayer must remain independent from WPF:\n" + string.Join("\n", violations));
    }

    private static void Check(Type owner, string member, Type? dependency, ICollection<string> violations)
    {
        if (dependency is null)
            return;
        if (ContainsWpfType(dependency))
            violations.Add($"{owner.FullName}: {member} -> {dependency.FullName}");
    }

    private static bool ContainsWpfType(Type type)
    {
        if (type.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal) == true)
            return true;
        if (type.HasElementType && type.GetElementType() is Type element && ContainsWpfType(element))
            return true;
        return type.IsGenericType && type.GetGenericArguments().Any(ContainsWpfType);
    }
}
