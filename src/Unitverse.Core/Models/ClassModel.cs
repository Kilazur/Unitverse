﻿namespace Unitverse.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Unitverse.Core.Assets;
    using Unitverse.Core.Frameworks;
    using Unitverse.Core.Helpers;
    using Unitverse.Core.Options;

    public class ClassModel : ITypeSymbolProvider
    {
        public ClassModel(TypeDeclarationSyntax declaration, SemanticModel semanticModel, bool isSingleItem)
        {
            Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
            SemanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
            IsSingleItem = isSingleItem;
            TargetFieldName = "_testClass";
            var typeSymbol = SemanticModel.GetDeclaredSymbol(declaration);

            if (typeSymbol == null)
            {
                throw new InvalidOperationException("Could not find the type symbol for the type '" + ClassName + "'.");
            }

            TypeSymbol = typeSymbol;

            TypeSyntax = SyntaxFactory.ParseTypeName(TypeSymbol.ToDisplayString(new SymbolDisplayFormat(
                SymbolDisplayGlobalNamespaceStyle.Omitted,
                SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
                SymbolDisplayGenericsOptions.IncludeTypeParameters,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes)));

            DependencyMap = ConstructorFieldAssignmentExtractor.ExtractMapFrom(declaration, semanticModel);

            foreach (var interfaceImpl in TypeSymbol.AllInterfaces)
            {
                foreach (var interfaceMember in interfaceImpl.GetMembers())
                {
                    if (interfaceMember != null)
                    {
                        try
                        {
                            var implementation = TypeSymbol.FindImplementationForInterfaceMember(interfaceMember);
                            if (implementation != null)
                            {
                                if (!_implementedInterfaceSymbols.TryGetValue(implementation, out var list))
                                {
                                    _implementedInterfaceSymbols[implementation] = list = new List<ISymbol>();
                                }

                                list.Add(interfaceMember);
                            }
                        }
                        catch (NullReferenceException)
                        {
                            // NRE happens in Roslyn when calling TypeSymbol.FindImplementationForInterfaceMember sometimes (https://github.com/mattwhitfield/Unitverse/issues/219)
                        }
                    }
                }
            }
        }

        public ClassDependencyMap DependencyMap { get; }

        public INamedTypeSymbol TypeSymbol { get; }

        public string ClassName => Declaration.GetClassName();

        public IList<IConstructorModel> Constructors { get; } = new List<IConstructorModel>();

        public TypeDeclarationSyntax Declaration { get; }

        public IConstructorModel? DefaultConstructor { get; set; }

        public IList<IIndexerModel> Indexers { get; } = new List<IIndexerModel>();

        public bool ShouldGenerate { get; set; } = true;

        public bool IsSingleItem { get; }

        public string TargetFieldName { get; private set; }

        public bool IsStatic => Declaration.Modifiers.Any(x => string.Equals(x.ValueText, "static", StringComparison.OrdinalIgnoreCase));

        public bool IsPublic => Declaration.Modifiers.Any(x => string.Equals(x.ValueText, "public", StringComparison.OrdinalIgnoreCase));

        public IList<IMethodModel> Methods { get; } = new List<IMethodModel>();

        public IList<IOperatorModel> Operators { get; } = new List<IOperatorModel>();

        public IList<IPropertyModel> Properties { get; } = new List<IPropertyModel>();

        public IList<TargetAsset> RequiredAssets { get; } = new List<TargetAsset>();

        public IList<IInterfaceModel> Interfaces { get; } = new List<IInterfaceModel>();

        public SemanticModel SemanticModel { get; }

        public ExpressionSyntax TargetInstance { get; set; } = SyntaxFactory.IdentifierName("_testClass");

        public TypeSyntax TypeSyntax { get; set; }

        public IList<UsingDirectiveSyntax> Usings { get; } = new List<UsingDirectiveSyntax>();

        private readonly IDictionary<ISymbol, IList<ISymbol>> _implementedInterfaceSymbols = new Dictionary<ISymbol, IList<ISymbol>>();

        public IList<ISymbol> GetImplementedInterfaceSymbolsFor(ISymbol symbol)
        {
            if (symbol != null && _implementedInterfaceSymbols.TryGetValue(symbol, out var list))
            {
                return list;
            }

            return new List<ISymbol>();
        }

        public void SetShouldGenerateForSingleItem(SyntaxNode syntaxNode)
        {
            ShouldGenerate = Declaration == syntaxNode;
            Methods.Each(x => x.SetShouldGenerateForSingleItem(syntaxNode));
            Operators.Each(x => x.SetShouldGenerateForSingleItem(syntaxNode));
            Properties.Each(x => x.SetShouldGenerateForSingleItem(syntaxNode));
            Constructors.Each(x => x.SetShouldGenerateForSingleItem(syntaxNode));
            Indexers.Each(x => x.SetShouldGenerateForSingleItem(syntaxNode));
        }

        public bool ShouldGenerateOrContainsItemThatShouldGenerate()
        {
            return ShouldGenerate ||
                Methods.Any(x => x.ShouldGenerate) ||
                Operators.Any(x => x.ShouldGenerate) ||
                Properties.Any(x => x.ShouldGenerate) ||
                Constructors.Any(x => x.ShouldGenerate) ||
                Indexers.Any(x => x.ShouldGenerate);
        }

        public string GetConstructorParameterFieldName(ParameterModel model, IFrameworkSet frameworkSet)
        {
            if (model is null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (frameworkSet is null)
            {
                throw new ArgumentNullException(nameof(frameworkSet));
            }

            return GetConstructorParameterFieldName(model.Name, model.TypeInfo, frameworkSet);
        }

        public string GetConstructorParameterFieldName(IPropertyModel model, IFrameworkSet frameworkSet)
        {
            if (model is null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            return GetConstructorParameterFieldName(model.Name, model.TypeInfo, frameworkSet);
        }

        private string GetConstructorParameterFieldName(string parameterName, TypeInfo typeInfo, IFrameworkSet frameworkSet)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                throw new ArgumentNullException(nameof(parameterName));
            }

            var baseNamingContext = new NamingContext(ClassName);

            var namingContext = baseNamingContext.WithParameterName(parameterName);

            var nameResolver = typeInfo.ShouldUseMock() ?
                frameworkSet.NamingProvider.MockDependencyFieldName :
                frameworkSet.NamingProvider.DependencyFieldName;

            var baseFieldName = nameResolver.Resolve(namingContext);
            if (frameworkSet.Options.GenerationOptions.UseFieldForAutoFixture)
            {
                var autoFixtureFieldName = frameworkSet.NamingProvider.AutoFixtureFieldName.Resolve(baseNamingContext);
                if (string.Equals(baseFieldName, autoFixtureFieldName, StringComparison.Ordinal))
                {
                    var changedNamingContext = baseNamingContext.WithParameterName(parameterName + "Param");
                    baseFieldName = nameResolver.Resolve(changedNamingContext);
                }
            }

            if (Constructors.SelectMany(x => x.Parameters).Where(x => string.Equals(x.Name, parameterName, StringComparison.OrdinalIgnoreCase)).Select(x => x.Type).Distinct().Count() < 2)
            {
                return baseFieldName;
            }

            if (typeInfo.Type is INamedTypeSymbol namedType)
            {
                return baseFieldName + GetFormattedName(namedType);
            }

            return baseFieldName + "UnknownType";
        }

        private static string GetFormattedName(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                var genericArguments = namedType.TypeArguments.Select(x => GetFormattedName(x)).Aggregate((x, y) => $"{x}{y}");
                return string.Concat(namedType.Name, genericArguments);
            }

            return type.Name;
        }

        public string GetIndexerName(IIndexerModel indexer)
        {
            if (indexer == null)
            {
                throw new ArgumentNullException(nameof(indexer));
            }

            if (Indexers.Count < 2)
            {
                return "Indexer";
            }

            return "IndexerFor" + indexer.Parameters.Select(x => x.TypeInfo.Type).WhereNotNull().Select(x => x.GetLastNamePart().ToPascalCase()).Aggregate((x, y) => x + "And" + y);
        }

        public ExpressionSyntax GetConstructorFieldReference(IPropertyModel model, IFrameworkSet frameworkSet)
        {
            return GetConstructorFieldReference(model.Name, model.TypeInfo, frameworkSet);
        }

        public ExpressionSyntax GetConstructorFieldReference(ParameterModel model, IFrameworkSet frameworkSet)
        {
            return GetConstructorFieldReference(model.Name, model.TypeInfo, frameworkSet);
        }

        private ExpressionSyntax GetConstructorFieldReference(string name, TypeInfo typeInfo, IFrameworkSet frameworkSet)
        {
            var identifierName = SyntaxFactory.IdentifierName(GetConstructorParameterFieldName(name, typeInfo, frameworkSet));

            var fieldSyntax = frameworkSet.QualifyFieldReference(identifierName);

            if (typeInfo.ShouldUseMock())
            {
                return frameworkSet.MockingFramework.GetFieldReference(fieldSyntax);
            }

            return fieldSyntax;
        }

        public ExpressionSyntax GetObjectCreationExpression(IFrameworkSet frameworkSet, bool forSetupMethod)
        {
            if (frameworkSet == null)
            {
                throw new ArgumentNullException(nameof(frameworkSet));
            }

            var targetConstructor = DefaultConstructor ?? Constructors.OrderByDescending(x => x.Parameters.Count).FirstOrDefault();

            if (forSetupMethod)
            {
                var mockedCreationExpression = frameworkSet.MockingFramework.GetObjectCreationExpression(TypeSyntax);
                if (mockedCreationExpression != null)
                {
                    return mockedCreationExpression;
                }
            }

            var objectCreation = SyntaxFactory.ObjectCreationExpression(TypeSyntax);

            if (targetConstructor != null && targetConstructor.Parameters.Count > 0)
            {
                return objectCreation.WithArgumentList(Generate.Arguments(targetConstructor.Parameters.Select(x => GetConstructorFieldReference(x, frameworkSet))));
            }

            if (targetConstructor == null && Properties.Any(x => x.HasInit))
            {
                var initializableProperties = Properties.Where(x => x.HasInit).ToList();
                if (initializableProperties.Any())
                {
                    return Generate.ObjectCreation(TypeSyntax, initializableProperties.Select(x => Generate.Assignment(x.Name, GetConstructorFieldReference(x, frameworkSet))));
                }
            }

            if (targetConstructor != null || !Declaration.ChildNodes().OfType<ConstructorDeclarationSyntax>().Any())
            {
                return objectCreation.WithArgumentList(SyntaxFactory.ArgumentList());
            }

            return AssignmentValueHelper.GetDefaultAssignmentValue(TypeSymbol, SemanticModel, frameworkSet);
        }

        public string GetMethodUniqueName(IMethodModel method)
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (Methods.Count(x => x.OriginalName == method.OriginalName) == 1)
            {
                return method.OriginalName;
            }

            var parameters = new List<string>();

            var hasEquallyNamedOverload = Methods.Any(x => x != method && x.Parameters.Count == method.Parameters.Count && x.Parameters.Select(p => p.Name.ToPascalCase()).SequenceEqual(method.Parameters.Select(p => p.Name.ToPascalCase())));
            var hasEquallyTypedOverload = Methods.Any(x => x != method && x.Parameters.Count == method.Parameters.Count && x.Parameters.Select(p => p.Type.ToPascalCase()).SequenceEqual(method.Parameters.Select(p => p.Type.ToPascalCase())));

            if (hasEquallyTypedOverload && method.Node?.TypeParameterList != null && method.Node.TypeParameterList.Parameters.Count > 0)
            {
                parameters.AddRange(method.Node.TypeParameterList.Parameters.Select(x => x.Identifier.ValueText));
            }

            for (int i = 0; i < method.Parameters.Count; i++)
            {
                var hasEquallyNamedParameter = Methods.Any(x => x != method && x.Parameters.Count == method.Parameters.Count && string.Equals(x.Parameters[i].Name, method.Parameters[i].Name, StringComparison.OrdinalIgnoreCase));
                if (hasEquallyNamedOverload && hasEquallyNamedParameter)
                {
                    var type = method.Parameters[i].TypeInfo.Type;
                    if (type != null)
                    {
                        parameters.Add(type.ToIdentifierName().ToPascalCase());
                    }
                }
                else
                {
                    parameters.Add(method.Parameters[i].Name.ToPascalCase());
                }
            }

            var baseName = method.OriginalName;
            if (method.Node?.ExplicitInterfaceSpecifier != null)
            {
                baseName += "For" + Generate.CleanName(method.Node.ExplicitInterfaceSpecifier.Name.ToString());
            }

            return string.Format(CultureInfo.InvariantCulture, "{0}With{1}", baseName, parameters.Any() ? parameters.Select(x => x.ToPascalCase()).Aggregate((x, y) => x + "And" + y) : "NoParameters");
        }

        public string GetOperatorUniqueName(IOperatorModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (Operators.Count(x => x.OriginalName == model.OriginalName) == 1)
            {
                return model.OriginalName;
            }

            return string.Format(CultureInfo.InvariantCulture, "{0}With{1}", model.OriginalName, model.Parameters.Any() ? model.Parameters.Select(x => x.TypeInfo.Type).WhereNotNull().Select(x => x.ToIdentifierName().ToPascalCase()).Aggregate((x, y) => x + "And" + y) : "NoParameters");
        }

        internal void SetTargetInstance(string fieldName, IFrameworkSet frameworkSet)
        {
            TargetFieldName = fieldName;
            var nameSyntax = SyntaxFactory.IdentifierName(TargetFieldName);

            TargetInstance = frameworkSet.QualifyFieldReference(nameSyntax);
        }
    }
}