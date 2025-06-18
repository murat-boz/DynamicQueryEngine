using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace DynamicQueryEngine.Core.Services
{
    public static class DynamicAnonymousTypeFactory
    {
        private static readonly ModuleBuilder ModuleBuilder;
        private static readonly ConcurrentDictionary<string, Type> Cache = new();

        static DynamicAnonymousTypeFactory()
        {
            var assemblyName = new AssemblyName("DynamicQueryEngine_AnonymousTypes");
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            ModuleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
        }

        public static Type CreateAnonymousType(List<string> propertyNames, Type baseModelType)
        {
            // Cache key → Property names + underlying property types
            var cacheKey = string.Join("|", propertyNames.Select(p => p.ToLowerInvariant()));
            if (Cache.TryGetValue(cacheKey, out var cachedType))
                return cachedType;

            var typeBuilder = ModuleBuilder.DefineType(
                "AnonType_" + Guid.NewGuid(),
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

            foreach (var propName in propertyNames)
            {
                var baseProp = baseModelType.GetProperty(propName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (baseProp == null)
                    throw new InvalidOperationException($"Property '{propName}' not found on type {baseModelType.Name}");

                var field = typeBuilder.DefineField("_" + propName, baseProp.PropertyType, FieldAttributes.Private);

                var propBuilder = typeBuilder.DefineProperty(propName, PropertyAttributes.HasDefault, baseProp.PropertyType, null);

                var getter = typeBuilder.DefineMethod(
                    "get_" + propName,
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    baseProp.PropertyType, Type.EmptyTypes);

                var getterIL = getter.GetILGenerator();
                getterIL.Emit(OpCodes.Ldarg_0);
                getterIL.Emit(OpCodes.Ldfld, field);
                getterIL.Emit(OpCodes.Ret);

                var setter = typeBuilder.DefineMethod(
                    "set_" + propName,
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    null, new[] { baseProp.PropertyType });

                var setterIL = setter.GetILGenerator();
                setterIL.Emit(OpCodes.Ldarg_0);
                setterIL.Emit(OpCodes.Ldarg_1);
                setterIL.Emit(OpCodes.Stfld, field);
                setterIL.Emit(OpCodes.Ret);

                propBuilder.SetGetMethod(getter);
                propBuilder.SetSetMethod(setter);
            }

            var resultType = typeBuilder.CreateType();

            Cache.TryAdd(cacheKey, resultType);
            return resultType;
        }
    }
}
