﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DtoGenerator.Logic.Infrastructure.TreeProcessing;
using DtoGenerator.Logic.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DtoGenerator.Logic.Infrastructure
{
    public class EntityParser
    {
        private static List<Type> _simpleTypes = new List<Type>()
        {
            typeof(DateTime),
            typeof(TimeSpan),
            typeof(Guid),
            typeof(byte), typeof(System.Byte),
            typeof(sbyte), typeof(System.SByte),
            typeof(char), typeof(System.Char),
            typeof(decimal), typeof(System.Decimal),
            typeof(double), typeof(System.Double),
            typeof(float), typeof(System.Single),
            typeof(int), typeof(System.Int32),
            typeof(uint), typeof(System.UInt32),
            typeof(long), typeof(System.Int64),
            typeof(ulong), typeof(System.UInt64),
            typeof(short), typeof(System.Int16),
            typeof(ushort), typeof(System.UInt16),
            typeof(string), typeof(System.String)
        };

        public static EntityMetadata FromString(string code)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            return FromSyntaxTree(syntaxTree);
        }

        public static async Task<EntityMetadata> FromDocument(Document doc, bool includeInherited = false)
        {
            var syntaxTree = doc.GetSyntaxTreeAsync().Result;
            var metadata = FromSyntaxTree(syntaxTree);

            if(includeInherited)
            {
                var baseDoc = await doc.GetRelatedEntityDocument(metadata.BaseClassName);

                if (baseDoc != null)
                {
                    var baseMetadata = await EntityParser.FromDocument(baseDoc, includeInherited: true);
                    foreach (var prop in baseMetadata.Properties)
                        prop.IsInherited = true;

                    metadata.Properties.AddRange(baseMetadata.Properties);
                }
            }

            return metadata;
        }

        public static async Task<bool> HasBaseDto(Document existingDto, string baseDtoName)
        {
            if (existingDto == null)
                return false;

            var existingRoot = await existingDto.GetSyntaxRootAsync();

            var finder = new BaseDtoClassLocator();
            finder.Visit(existingRoot);

            return finder.BaseDtoName == baseDtoName;
        }

        public static async Task<bool> HasDataContract(Document existingDto)
        {
            if (existingDto == null)
                return false;

            var existingRoot = await existingDto.GetSyntaxRootAsync();

            return existingRoot.ToString().Contains("[DataContract]");
        }

        public static async Task<List<string>> GetAutoGeneratedProperties(Document existingDto)
        {
            if (existingDto == null)
                return null;

            var existingRoot = await existingDto.GetSyntaxRootAsync();

            var finder = new CustomCodeLocator();
            finder.Visit(existingRoot);

            var propFinder = new GeneratedPropertiesEnumerator(finder);
            propFinder.Visit(existingRoot);

            return propFinder.GeneratedProperties;
        }

        private static EntityMetadata FromSyntaxTree(SyntaxTree syntaxTree)
        {
            var root = syntaxTree.GetRoot();

            var classNodes = root
                .DescendantNodes(p => !(p is ClassDeclarationSyntax))
                .OfType<ClassDeclarationSyntax>();

            if (classNodes.Count() != 1)
            {
                throw new ArgumentException("Source code to parse must contain exactly one class declaration!");
            }

            var namespaceNode = root
                .DescendantNodes(p => !(p is NamespaceDeclarationSyntax))
                .OfType<NamespaceDeclarationSyntax>()
                .FirstOrDefault();

            var classNode = classNodes
                .Single();

            var properties = classNode
                .DescendantNodes(p => !(p is PropertyDeclarationSyntax))
                .OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Modifiers.Any(m => m.Kind() == SyntaxKind.PublicKeyword))
                .Where(p => p.AccessorList != null)
                .Where(p => p.AccessorList.Accessors.Any(a => a.Kind() == SyntaxKind.GetAccessorDeclaration))
                .Where(p => p.AccessorList.Accessors.Any(a => a.Kind() == SyntaxKind.SetAccessorDeclaration));

            var result = new EntityMetadata();
            result.Name = classNode.Identifier.Text;
            result.Namespace = namespaceNode.Name.ToString();

            if(classNode.BaseList != null && classNode.BaseList.Types.Count > 0)
            {
                var baseType = classNode.BaseList.Types.First().ToString();
                var isInterface = baseType.Length > 2 && baseType.StartsWith("I") && char.IsUpper(baseType[1]);

                if(!isInterface)
                {
                    result.BaseClassName = baseType;
                    result.BaseClassDtoName = baseType + "DTO";
                }
            }

            result.Properties = properties
                .Select(p => new PropertyMetadata()
                {
                    Type = p.Type.ToString(),
                    Name = p.Identifier.Text,
                    IsSimpleProperty = IsSimpleProperty(p),
                    SyntaxNode = p,
                    IsCollection = IsCollection(p),
                    IsRelation = IsRelation(p),
                    RelatedEntityName = IsRelation(p) ? GetRelatedEntity(p) : null
                })
                .ToList();

            return result;
        }
        

        private static string GetRelatedEntity(PropertyDeclarationSyntax p)
        {
            try
            {
                if (p.Type is GenericNameSyntax)
                    return (p.Type as GenericNameSyntax).TypeArgumentList.Arguments.OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text).Single();

                if (p.Type is IdentifierNameSyntax)
                    return (p.Type as IdentifierNameSyntax).Identifier.Text;
            }
            catch(Exception)
            {
                return null;
            }

            return null;
        }

        private static bool IsRelation(PropertyDeclarationSyntax p)
        {
            return !IsSimpleProperty(p);
        }

        private static bool IsCollection(PropertyDeclarationSyntax p)
        {
            var genericSyntax = p.Type as GenericNameSyntax;
            if (genericSyntax != null && genericSyntax.Identifier.ToString() != "Nullable")
                return true;

            return false;
        }

        private static bool IsSimpleProperty(PropertyDeclarationSyntax propertyNode)
        {
            var nullableType = propertyNode.Type as NullableTypeSyntax;
            if(nullableType != null)
            {
                return IsSimpleType(nullableType.ElementType);
            }

            var nullableGenericType = propertyNode.Type as GenericNameSyntax;
            if (nullableGenericType != null && nullableGenericType.Identifier.ToString() == "Nullable")
                return IsSimpleType(nullableGenericType.TypeArgumentList.Arguments.First());

            return IsSimpleType(propertyNode.Type);
        }

        private static bool IsSimpleType(TypeSyntax type)
        {
            var simpleTypeList = _simpleTypes
                .Select(p => $"{p.Namespace}.{p.Name}")
                .Concat(_simpleTypes.Select(p => p.Name))
                .ToList();

            if (simpleTypeList.Contains(type.ToString()))
                return true;

            if (type is PredefinedTypeSyntax)
                return true;

            return false;
        }
    }
}
