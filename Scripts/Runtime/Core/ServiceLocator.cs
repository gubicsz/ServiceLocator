using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
#if UNITASK_ENABLED
using System.Threading;
using Cysharp.Threading.Tasks;
#endif


namespace BrunoMikoski.ServicesLocation
{
    public class ServiceLocator 
    {
        private static ServiceLocator instance;
        public static ServiceLocator Instance
        {
            get
            {
                if (instance == null)
                    instance = new ServiceLocator();
                return instance;
            }
        }

        private Dictionary<Type, object> typeToInstances = new();

        private Dictionary<Type, List<IServiceObservable>> typeToObservables = new();

        private List<object> waitingOnDependenciesTobeResolved = new();
        
        private Dictionary<List<Type>, Action> servicesListToCallback = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ClearStaticReferences()
        {
            instance = null;
        }
        
        public void RegisterInstance<T>(T instance)
        {
            Type type = typeof(T);
            RegisterInstance(type, instance);
        }

        private void RegisterInstance(Type type, object instance)
        {
            if (!CanRegisterService(type, instance))
                return;

            if (!IsServiceDependenciesResolved(type))
            {
                if (!waitingOnDependenciesTobeResolved.Contains(instance))
                    waitingOnDependenciesTobeResolved.Add(instance);
                
                return;
            }
            
            typeToInstances.Add(type, instance);
            TryResolveDependencies();
            DispatchOnRegistered(type, instance);
        }

        private void DispatchOnRegistered(Type type, object instance)
        {
            if (instance is IOnServiceRegistered onRegistered)
            {
                onRegistered.OnRegisteredOnServiceLocator(this);
            }

            if (typeToObservables.TryGetValue(type, out List<IServiceObservable> observables))
            {
                for (int i = 0; i < observables.Count; i++)
                    observables[i].OnServiceRegistered(type);
            }
        }

        private bool CanRegisterService(Type type, object instance)
        {
            if (HasService(type))
            {
                Debug.LogError($"Service of type {type} is already registered.");
                return false;
            }

            if (instance is IConditionalService conditionalService)
            {
                if (!conditionalService.CanBeRegistered(this))
                    return false;
            }

            return true;
        }

        public bool HasService<T>()
        {
            return HasService(typeof(T));
        }

        private bool HasService(Type type)
        {
            return typeToInstances.ContainsKey(type);
        }

        public T GetInstance<T>() where T : class
        {
            Type type = typeof(T);
            return (T) GetRawInstance(type);
        }

        public object GetRawInstance(Type targetType)
        {
            if (typeToInstances.TryGetValue(targetType, out object instanceObject))
                return instanceObject;

            if (Application.isPlaying)
            {
                Debug.LogError(
                    $"The Service {targetType} is not yet registered on the ServiceLocator, " +
                    $"consider implementing IDependsOnExplicitServices interface");
            }
            else
            {
                if (targetType.IsSubclassOf(typeof(Object)))
                {
                    Object instanceType = Object.FindObjectOfType(targetType);
                    if (instanceType != null)
                        return instanceType;
#if UNITY_EDITOR
                    string[] guids = UnityEditor.AssetDatabase.FindAssets($"{targetType} t:Prefab");
                    if (guids.Length > 0)
                    {
                        return UnityEditor.AssetDatabase.LoadAssetAtPath(
                            UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]), targetType);
                    }
#endif                    
                    Debug.LogError($"Failed to find any Object of type: {targetType}, check if the object you need is available on the scene");
                    return null;
                }

                object instance = Activator.CreateInstance(targetType);
                return instance;
            }
            
            return null;
        }
        

        public void UnregisterAllServices()
        {
            List<object> activeInstances = new List<object>(typeToInstances.Count);
            foreach (KeyValuePair<Type, object> typeToInstance in typeToInstances)
                activeInstances.Add(typeToInstance.Value);

            for (int i = activeInstances.Count - 1; i >= 0; i--)
            {
                UnregisterInstance(activeInstances[i]);
            }
            
            typeToInstances.Clear();

            if (waitingOnDependenciesTobeResolved.Count > 0)
            {
                Debug.LogWarning($"{waitingOnDependenciesTobeResolved.Count} dependencies was waiting to be resolved");
                waitingOnDependenciesTobeResolved.Clear();
                typeToObservables.Clear();
            }
        }
        
        public void UnregisterInstance<T>()
        {
            Type type = typeof(T);
            UnregisterInstance(type);
        }
        
        public void UnregisterInstance<T>(T instance)
        {
            Type type = instance.GetType();
            UnregisterInstance(type);
        }

        public void UnregisterInstance(Type targetType)
        {
            if (!typeToInstances.TryGetValue(targetType, out object serviceInstance)) 
                return;
            
            DispatchOnUnregisteredService(targetType, serviceInstance);
            typeToInstances.Remove(targetType);
        }

        private void DispatchOnUnregisteredService(Type targetType, object serviceInstance)
        {
            if (serviceInstance is IOnServiceUnregistered onServiceUnregistered)
            {
                onServiceUnregistered.OnUnregisteredFromServiceLocator(this);
            }
            
            if (typeToObservables.TryGetValue(targetType, out List<IServiceObservable> observables))
            {
                for (int i = 0; i < observables.Count; i++)
                    observables[i].OnServiceUnregistered(targetType);
            }
        }

        public void SubscribeToServiceChanges<T>(IServiceObservable observable)
        {
            Type type = typeof(T);
            if (!typeToObservables.ContainsKey(type))
                typeToObservables.Add(type, new List<IServiceObservable>());

            if (!typeToObservables[type].Contains(observable))
                typeToObservables[type].Add(observable);
        }
        
        public void UnsubscribeToServiceChanges<T>(IServiceObservable observable)
        {
            Type type = typeof(T);
            if (!typeToObservables.TryGetValue(type, out List<IServiceObservable> observables))
                return;

            observables.Remove(observable);
        }

        private void TryResolveDependencies()
        {
            for (int i = waitingOnDependenciesTobeResolved.Count - 1; i >= 0; i--)
            {
                object waitingObject = waitingOnDependenciesTobeResolved[i];
                
                if (!IsServiceDependenciesResolved(waitingObject.GetType())) 
                    continue;
                
                waitingOnDependenciesTobeResolved.Remove(waitingObject);
                RegisterInstance(waitingObject);
            }

            foreach (var listToCallback in servicesListToCallback)
            {
                if (!HasAllServices(listToCallback.Key))
                    continue;
                
                listToCallback.Value.Invoke();
                servicesListToCallback.Remove(listToCallback.Key);
            }
        }

        private bool IsServiceDependenciesResolved(Type targetType)
        {
            object[] serviceAttributeObjects = targetType.GetCustomAttributes(
                typeof(ServiceImplementationAttribute), true);

            if (serviceAttributeObjects.Length == 0)
                return true;

            for (int i = 0; i < serviceAttributeObjects.Length; i++)
            {
                ServiceImplementationAttribute serviceAttribute =
                    (ServiceImplementationAttribute) serviceAttributeObjects[i];

                if (serviceAttribute.DependsOn == null || serviceAttribute.DependsOn.Length == 0)
                    continue;

                for (int j = 0; j < serviceAttribute.DependsOn.Length; j++)
                {
                    Type dependencyType = serviceAttribute.DependsOn[j];
                    if (!HasService(dependencyType))
                        return false;
                }
            }

            return true;
        }

        public void Inject(object targetObject, Action callback = null)
        {
            bool allResolved = DependenciesUtility.Inject(targetObject);
            if (allResolved)
            {
                if (targetObject is IOnInjected onInjected)
                    onInjected.OnInjected();
               
                return;
            }
           
            if (callback == null)
            {
                throw new Exception(
                    $"{targetObject.GetType().Name} has unresolved dependencies and no callback was provided to handle it");
            }
               
            List<Type> unresolvedDependencies = DependenciesUtility.GetUnresolvedDependencies(targetObject);
               
            AddServicesRegisteredCallback(unresolvedDependencies, () =>
            {
                if (targetObject is IOnInjected onInjected)
                    onInjected.OnInjected();

                callback.Invoke();
            });
        }

#if UNITASK_ENABLED

        public async UniTask InjectAsync(object script, CancellationToken token = default)
        {
            bool allResolved = DependenciesUtility.Inject(script);
            if (allResolved)
                return;
            
            List<Type> unresolvedDependencies = DependenciesUtility.GetUnresolvedDependencies(script);

            if (token == default)
            {
                if (script is MonoBehaviour unityObject)
                    token = unityObject.GetCancellationTokenOnDestroy();
            }

            List<UniTask> dependenciesTasks = new List<UniTask>();

            for (int i = 0; i < unresolvedDependencies.Count; i++)
            {
                Type unresolvedDependency = unresolvedDependencies[i];
                dependenciesTasks.Add(WaitForServiceAsync(unresolvedDependency, token));
            }

            await UniTask.WhenAll(dependenciesTasks);

            if (script is IOnInjected onInjected)
                onInjected.OnInjected();
        }
        
        
        public async UniTask WaitForServiceAsync<T>(CancellationToken token = default) where T : class
        {
            await WaitForServiceAsync(typeof(T), token);
        }
        
        public async UniTask WaitForServiceAsync(Type targetType, CancellationToken token = default)
        {
            await UniTask.WaitUntil(() => HasService(targetType), cancellationToken: token);
        }
#endif
        private void AddServicesRegisteredCallback(List<Type> services, Action callback)
        {
            if (HasAllServices(services))
                callback?.Invoke();

            servicesListToCallback.Add(services, callback);
        }

        private bool HasAllServices(List<Type> services)
        {
            for (int i = 0; i < services.Count; i++)
            {
                if (!HasService(services[i]))
                    return false;
            }

            return true;
        }
    }
}
