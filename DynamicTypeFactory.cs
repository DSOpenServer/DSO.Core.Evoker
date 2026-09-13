using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace DSO.Core.Evoker
{
    // --- Reflection.Emit ile Roslyn'siz Dinamik Class Üretici ---
    public static class DynamicTypeFactory
    {
        public static Type CreateType(string className, Dictionary<string, Type> properties)
        {
            var assemblyName = new AssemblyName($"EvokerDynamicAssembly_{Guid.NewGuid():N}");
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
            var typeBuilder = moduleBuilder.DefineType(className, TypeAttributes.Public | TypeAttributes.Class);

            foreach (var prop in properties)
            {
                string propName = prop.Key;
                Type propType = prop.Value;

                // Backing field
                FieldBuilder fieldBuilder = typeBuilder.DefineField($"_{propName.ToLower()}", propType, FieldAttributes.Private);
                PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(propName, PropertyAttributes.HasDefault, propType, null);

                // Getter: public T get_PropName() => _propName;
                MethodBuilder getMethodBuilder = typeBuilder.DefineMethod(
                    $"get_{propName}",
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    propType,
                    Type.EmptyTypes);

                ILGenerator getIL = getMethodBuilder.GetILGenerator();
                getIL.Emit(OpCodes.Ldarg_0);
                getIL.Emit(OpCodes.Ldfld, fieldBuilder);
                getIL.Emit(OpCodes.Ret);

                // Setter: public void set_PropName(T value) => _propName = value;
                MethodBuilder setMethodBuilder = typeBuilder.DefineMethod(
                    $"set_{propName}",
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    null,
                    new[] { propType });

                ILGenerator setIL = setMethodBuilder.GetILGenerator();
                setIL.Emit(OpCodes.Ldarg_0);
                setIL.Emit(OpCodes.Ldarg_1);
                setIL.Emit(OpCodes.Stfld, fieldBuilder);
                setIL.Emit(OpCodes.Ret);

                propertyBuilder.SetGetMethod(getMethodBuilder);
                propertyBuilder.SetSetMethod(setMethodBuilder);
            }

            return typeBuilder.CreateType()!;
        }

        public static Type OldCreateType(string className, System.Collections.Generic.Dictionary<string, Type> properties)
        {
            var assemblyName = new AssemblyName($"EvokerDynamicAssembly_{Guid.NewGuid():N}");
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
            var typeBuilder = moduleBuilder.DefineType(className, TypeAttributes.Public | TypeAttributes.Class);

            foreach (var prop in properties)
            {
                string propName = prop.Key;
                Type propType = prop.Value;

                FieldBuilder fieldBuilder = typeBuilder.DefineField($"_{propName.ToLower()}", propType, FieldAttributes.Private);
                PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(propName, PropertyAttributes.HasDefault, propType, null);

                // Getter
                MethodBuilder getMethodBuilder = typeBuilder.DefineMethod($"get_{propName}",
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, propType, Type.EmptyTypes);
                ILGenerator getIL = getMethodBuilder.GetILGenerator();
                getIL.Emit(OpCodes.Ldarg_0);
                getIL.Emit(OpCodes.Ldfld, fieldBuilder);
                getIL.Emit(OpCodes.Ret);

                // Setter
                MethodBuilder setMethodBuilder = typeBuilder.DefineMethod($"set_{propName}",
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, new[] { propType });
                ILGenerator setIL = setMethodBuilder.GetILGenerator();
                setIL.Emit(OpCodes.Ldarg_0);
                setIL.Emit(OpCodes.Ldarg_1);
                setIL.Emit(OpCodes.Stfld, fieldBuilder);
                setIL.Emit(OpCodes.Ret);

                propertyBuilder.SetGetMethod(getMethodBuilder);
                propertyBuilder.SetSetMethod(setMethodBuilder);
            }

            return typeBuilder.CreateType()!;
        }
    }
}
