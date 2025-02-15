﻿using System;
using System.Collections.Generic;
using System.Linq;
using Jenny;
using Jenny.Generator;
using DesperateDevs.Extensions;
using DesperateDevs.Reflection;
using DesperateDevs.Serialization;
using Entitas.CodeGeneration.Attributes;

namespace Entitas.CodeGeneration.Plugins
{
    public class ComponentDataProvider : IDataProvider, IConfigurable, ICachable, IDoctor
    {
        public string Name => "Component";
        public int Order => 0;
        public bool RunInDryMode => true;

        public Dictionary<string, string> DefaultProperties
        {
            get
            {
                var dataProviderProperties = _dataProviders
                    .OfType<IConfigurable>()
                    .Select(i => i.DefaultProperties)
                    .ToArray();

                return _assembliesConfig.DefaultProperties
                    .Merge(_contextsComponentDataProvider.DefaultProperties)
                    .Merge(_ignoreNamespacesConfig.DefaultProperties)
                    .Merge(dataProviderProperties);
            }
        }

        public Dictionary<string, object> ObjectCache { get; set; }

        readonly CodeGeneratorConfig _codeGeneratorConfig = new CodeGeneratorConfig();
        readonly AssembliesConfig _assembliesConfig = new AssembliesConfig();
        readonly ContextsComponentDataProvider _contextsComponentDataProvider = new ContextsComponentDataProvider();
        readonly IgnoreNamespacesConfig _ignoreNamespacesConfig = new IgnoreNamespacesConfig();

        static IComponentDataProvider[] getComponentDataProviders()
        {
            return new IComponentDataProvider[]
            {
                new ComponentTypeComponentDataProvider(),
                new MemberDataComponentDataProvider(),
                new ContextsComponentDataProvider(),
                new IsUniqueComponentDataProvider(),
                new FlagPrefixComponentDataProvider(),
                new ShouldGenerateComponentComponentDataProvider(),
                new ShouldGenerateMethodsComponentDataProvider(),
                new ShouldGenerateComponentIndexComponentDataProvider(),
                new EventComponentDataProvider()
            };
        }

        readonly Type[] _types;
        readonly IComponentDataProvider[] _dataProviders;

        public ComponentDataProvider() : this(null) { }

        public ComponentDataProvider(Type[] types) : this(types, getComponentDataProviders()) { }

        protected ComponentDataProvider(Type[] types, IComponentDataProvider[] dataProviders)
        {
            _types = types;
            _dataProviders = dataProviders;
        }

        public void Configure(Preferences preferences)
        {
            _codeGeneratorConfig.Configure(preferences);
            _assembliesConfig.Configure(preferences);
            foreach (var dataProvider in _dataProviders.OfType<IConfigurable>())
                dataProvider.Configure(preferences);

            _contextsComponentDataProvider.Configure(preferences);
            _ignoreNamespacesConfig.Configure(preferences);
        }

        public CodeGeneratorData[] GetData()
        {
            var types = _types ?? PluginUtil
                .GetCachedAssemblyResolver(ObjectCache, _assembliesConfig.assemblies, _codeGeneratorConfig.SearchPaths)
                .GetTypes();

            var dataFromComponents = types
                .Where(type => type.ImplementsInterface<IComponent>())
                .Where(type => !type.IsAbstract)
                .Select(type => createDataForComponent(type))
                .ToArray();

            var dataFromNonComponents = types
                .Where(type => !type.ImplementsInterface<IComponent>())
                .Where(type => !type.IsGenericType)
                .Where(type => hasContexts(type))
                .SelectMany(type => createDataForNonComponent(type))
                .ToArray();

            var mergedData = merge(dataFromNonComponents, dataFromComponents);

            var dataFromEvents = mergedData
                .Where(data => data.IsEvent())
                .SelectMany(data => createDataForEvents(data))
                .ToArray();

            return merge(dataFromEvents, mergedData);
        }

        ComponentData[] merge(ComponentData[] prioData, ComponentData[] redundantData)
        {
            var lookup = prioData.ToLookup(data => data.GetTypeName());
            return redundantData
                .Where(data => !lookup.Contains(data.GetTypeName()))
                .Concat(prioData)
                .ToArray();
        }

        ComponentData createDataForComponent(Type type)
        {
            var data = new ComponentData();
            foreach (var provider in _dataProviders)
                provider.Provide(type, data);

            return data;
        }

        ComponentData[] createDataForNonComponent(Type type) => getComponentNames(type)
            .Select(componentName =>
            {
                var data = createDataForComponent(type);
                data.SetTypeName(componentName.AddComponentSuffix());
                data.SetMemberData(new[]
                {
                    new MemberData(type.ToCompilableString(), "value")
                });

                return data;
            }).ToArray();

        ComponentData[] createDataForEvents(ComponentData data) => data.GetContextNames()
            .SelectMany(contextName =>
                data.GetEventData().Select(eventData =>
                {
                    var dataForEvent = new ComponentData(data);
                    dataForEvent.IsEvent(false);
                    dataForEvent.IsUnique(false);
                    dataForEvent.ShouldGenerateComponent(false);
                    var eventComponentName = data.EventComponentName(eventData);
                    var eventTypeSuffix = eventData.GetEventTypeSuffix();
                    var optionalContextName = dataForEvent.GetContextNames().Length > 1 ? contextName : string.Empty;
                    var listenerComponentName = optionalContextName + eventComponentName + eventTypeSuffix.AddListenerSuffix();
                    dataForEvent.SetTypeName(listenerComponentName.AddComponentSuffix());
                    dataForEvent.SetMemberData(new[]
                    {
                        new MemberData($"System.Collections.Generic.List<I{listenerComponentName}>", "value")
                    });
                    dataForEvent.SetContextNames(new[] {contextName});
                    return dataForEvent;
                }).ToArray()
            ).ToArray();

        bool hasContexts(Type type) => _contextsComponentDataProvider.GetContextNames(type).Length != 0;

        string[] getComponentNames(Type type)
        {
            var attr = Attribute
                .GetCustomAttributes(type)
                .OfType<ComponentNameAttribute>()
                .SingleOrDefault();

            if (attr == null)
                return new[] {type.ToCompilableString().ShortTypeName().AddComponentSuffix()};

            return attr.componentNames;
        }

        public Diagnosis Diagnose()
        {
            var isStandalone = AppDomain.CurrentDomain
                .GetAllTypes()
                .Any(type => type.FullName.StartsWith("Jenny.Generator.Cli"));

            if (isStandalone)
            {
                var typeName = typeof(ComponentDataProvider).FullName;
                if (_codeGeneratorConfig.DataProviders.Contains(typeName))
                {
                    return new Diagnosis(
                        $"{typeName} loads and reflects {string.Join(", ", _assembliesConfig.assemblies)} and therefore doesn't support server mode!",
                        $"Don't use the code generator in server mode with {typeName}",
                        DiagnosisSeverity.Hint
                    );
                }
            }

            return Diagnosis.Healthy;
        }

        public bool ApplyFix() => false;
    }
}
