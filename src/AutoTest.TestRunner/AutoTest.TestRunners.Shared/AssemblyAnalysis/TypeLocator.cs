﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Collections.Generic;

namespace AutoTest.TestRunners.Shared.AssemblyAnalysis
{
    public class SimpleTypeLocator
    {
        private string _assembly;
        private string _type;

        public SimpleTypeLocator(string assembly, string type)
        {
            _assembly = assembly;
            _type = type;
        }

        public SimpleType Locate()
        {
            var asm = AssemblyDefinition.ReadAssembly(_assembly);
            foreach (var module in asm.Modules)
            {
                var result = locateSimpleType(module.Types);
                if (result != null)
                    return result;
            }
            return null;
        }

        public SimpleType LocateParent()
        {
            var end = _type.LastIndexOf('.');
            if (end == -1)
                return null;
            _type = _type.Substring(0, end);
            return Locate();
        }

        private SimpleType locateSimpleType(Collection<TypeDefinition> types)
        {
            foreach (var type in types)
            {
                if (type.FullName.Equals(_type))
                    return getType(type);
                var result = locateSimpleType(type.NestedTypes);
                if (result != null)
                    return result;
                result = locateSimpleType(type.Methods, type.FullName);
                if (result != null)
                    return result;
            }
            return null;
        }

        private static SimpleType getType(TypeDefinition type)
        {
            return new SimpleType(
                TypeCategory.Class,
                type.FullName,
                type.CustomAttributes.Select(x => x.AttributeType.FullName),
                type.Methods.Select(x => new SimpleType(
                    TypeCategory.Method,
                    type.FullName + "." + x.Name,
                    x.CustomAttributes.Select(m => m.AttributeType.FullName),
                    new SimpleType[] { })));
        }

        private SimpleType locateSimpleType(Collection<MethodDefinition> methods, string typeFullname)
        {
            foreach (var method in methods)
            {
                var fullName = typeFullname + "." + method.Name;
                if (fullName.Equals(_type))
                    return new SimpleType(TypeCategory.Method, fullName, method.CustomAttributes.Select(x => x.AttributeType.FullName), new SimpleType[] {});
            }
            return null;
        }
    }
}
