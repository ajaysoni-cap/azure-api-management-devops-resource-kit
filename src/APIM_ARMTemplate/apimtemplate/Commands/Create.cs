using McMaster.Extensions.CommandLineUtils;
using System;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common;
using System.Collections.Generic;
using System.Linq;
using apimtemplate.Creator.Utilities;

namespace Microsoft.Azure.Management.ApiManagement.ArmTemplates.Create
{
    public class CreateCommand : CommandLineApplication
    {
        public CreateCommand()
        {
            Name = GlobalConstants.CreateName;
            Description = GlobalConstants.CreateDescription;

            // list command options
            CommandOption configFile = Option("--configFile <configFile>", "Config YAML file location", CommandOptionType.SingleValue).IsRequired();

            CommandOption appInsightsInstrumentationKey = Option("--appInsightsInstrumentationKey <appInsightsInstrumentationKey>", "AppInsights intrumentationkey", CommandOptionType.SingleValue);

            CommandOption appInsightsName = Option("--appInsightsName <appInsightsName>", "AppInsights Name", CommandOptionType.SingleValue);

            // list command options
            CommandOption backendurlconfigFile = Option("--backendurlconfigFile <backendurlconfigFile>", "backend url json file location", CommandOptionType.SingleValue);

            var mergeApiTemplatesOption = Option("--mergeApiTemplates <mergeApiTemplates>", "Merge templates", CommandOptionType.SingleValue);

            this.HelpOption();

            OnExecute(async () =>
            {
                // convert config file to CreatorConfig class
                FileReader fileReader = new FileReader();
                CreatorConfig creatorConfig = await fileReader.ConvertConfigYAMLToCreatorConfigAsync(configFile.Value());
                var mergeApiTemplates = Convert.ToBoolean(mergeApiTemplatesOption.Value());

                AppInsightsUpdater appInsightsUpdater = new AppInsightsUpdater();
                appInsightsUpdater.UpdateAppInsightNameAndInstrumentationKey(creatorConfig, appInsightsInstrumentationKey, appInsightsName);

                // validate creator config
                CreatorConfigurationValidator creatorConfigurationValidator = new CreatorConfigurationValidator(this);

                //if backendurlfile passed as parameter
                if (backendurlconfigFile != null && !string.IsNullOrEmpty(backendurlconfigFile.Value()))
                {
                    CreatorApiBackendUrlUpdater creatorApiBackendUrlUpdater = new CreatorApiBackendUrlUpdater();
                    creatorConfig = creatorApiBackendUrlUpdater.UpdateBackendServiceUrl(backendurlconfigFile.Value(), creatorConfig);
                }

                bool isValidCreatorConfig = creatorConfigurationValidator.ValidateCreatorConfig(creatorConfig);
                if (isValidCreatorConfig)
                {
                    // required parameters have been supplied

                    // initialize file helper classes
                    FileWriter fileWriter = new FileWriter();
                    FileNameGenerator fileNameGenerator = new FileNameGenerator();
                    FileNames fileNames = creatorConfig.baseFileName == null ? fileNameGenerator.GenerateFileNames(creatorConfig.apimServiceName) : fileNameGenerator.GenerateFileNames(creatorConfig.baseFileName);

                    // initialize template creator classes
                    APIVersionSetTemplateCreator apiVersionSetTemplateCreator = new APIVersionSetTemplateCreator();
                    LoggerTemplateCreator loggerTemplateCreator = new LoggerTemplateCreator();
                    BackendTemplateCreator backendTemplateCreator = new BackendTemplateCreator();
                    AuthorizationServerTemplateCreator authorizationServerTemplateCreator = new AuthorizationServerTemplateCreator();
                    ProductAPITemplateCreator productAPITemplateCreator = new ProductAPITemplateCreator();
                    TagAPITemplateCreator tagAPITemplateCreator = new TagAPITemplateCreator();
                    PolicyTemplateCreator policyTemplateCreator = new PolicyTemplateCreator(fileReader);
                    ProductGroupTemplateCreator productGroupTemplateCreator = new ProductGroupTemplateCreator();
                    DiagnosticTemplateCreator diagnosticTemplateCreator = new DiagnosticTemplateCreator();
                    ReleaseTemplateCreator releaseTemplateCreator = new ReleaseTemplateCreator();
                    ProductTemplateCreator productTemplateCreator = new ProductTemplateCreator(policyTemplateCreator, productGroupTemplateCreator);
                    PropertyTemplateCreator propertyTemplateCreator = new PropertyTemplateCreator();
                    TagTemplateCreator tagTemplateCreator = new TagTemplateCreator();
                    APITemplateCreator apiTemplateCreator = new APITemplateCreator(fileReader, policyTemplateCreator, productAPITemplateCreator, tagAPITemplateCreator, diagnosticTemplateCreator, releaseTemplateCreator);
                    MasterTemplateCreator masterTemplateCreator = new MasterTemplateCreator();

                    // create templates from provided configuration
                    Console.WriteLine("Creating global service policy template");
                    Console.WriteLine("------------------------------------------");
                    Template globalServicePolicyTemplate = creatorConfig.policy != null ? policyTemplateCreator.CreateGlobalServicePolicyTemplate(creatorConfig) : null;
                    Console.WriteLine("Creating API version set template");
                    Console.WriteLine("------------------------------------------");
                    Template apiVersionSetsTemplate = creatorConfig.apiVersionSets != null ? apiVersionSetTemplateCreator.CreateAPIVersionSetTemplate(creatorConfig) : null;
                    Console.WriteLine("Creating product template");
                    Console.WriteLine("------------------------------------------");
                    Template productsTemplate = creatorConfig.products != null ? productTemplateCreator.CreateProductTemplate(creatorConfig) : null;
                    Console.WriteLine("Creating named values template");
                    Console.WriteLine("------------------------------------------");
                    Template propertyTemplate = creatorConfig.namedValues != null ? propertyTemplateCreator.CreatePropertyTemplate(creatorConfig) : null;
                    Console.WriteLine("Creating logger template");
                    Console.WriteLine("------------------------------------------");
                    Template loggersTemplate = creatorConfig.loggers != null ? loggerTemplateCreator.CreateLoggerTemplate(creatorConfig) : null;
                    Console.WriteLine("Creating backend template");
                    Console.WriteLine("------------------------------------------");
                    Template backendsTemplate = creatorConfig.backends != null ? backendTemplateCreator.CreateBackendTemplate(creatorConfig) : null;
                    Console.WriteLine("Creating authorization server template");
                    Console.WriteLine("------------------------------------------");
                    Template authorizationServersTemplate = creatorConfig.authorizationServers != null ? authorizationServerTemplateCreator.CreateAuthorizationServerTemplate(creatorConfig) : null;

                    // store name and whether the api will depend on the version set template each api necessary to build linked templates
                    List<LinkedMasterTemplateAPIInformation> apiInformation = new List<LinkedMasterTemplateAPIInformation>();
                    List<Template> apiTemplates = new List<Template>();
                    if (mergeApiTemplates)
                    {
                        var operations = apiTemplateCreator.CreateEmptyTemplate();
                        operations.resources = apiVersionSetsTemplate.resources;
                        apiTemplates.Add(operations);   // api operations template

                        apiTemplates.Add(apiTemplateCreator.CreateEmptyTemplate());   // api properties template
                    }

                    Console.WriteLine("Creating API templates");
                    Console.WriteLine("------------------------------------------");
                    foreach (APIConfig api in creatorConfig.apis)
                    {
                        // create api templates from provided api config - if the api config contains a supplied apiVersion, split the templates into 2 for metadata and swagger content, otherwise create a unified template
                        List<Template> apiTemplateSet = await apiTemplateCreator.CreateAPITemplatesAsync(api, mergeApiTemplates);

                        if (!mergeApiTemplates)
                        {
                            apiTemplates.AddRange(apiTemplateSet);

                            // create the relevant info that will be needed to properly link to the api template(s) from the master template
                            apiInformation.Add(new LinkedMasterTemplateAPIInformation()
                            {
                                name = api.name,
                                isSplit = apiTemplateCreator.isSplitAPI(api),
                                dependsOnGlobalServicePolicies = creatorConfig.policy != null,
                                dependsOnVersionSets = api.apiVersionSetId != null,
                                dependsOnProducts = api.products != null,
                                dependsOnTags = api.tags != null,
                                dependsOnLoggers = await masterTemplateCreator.DetermineIfAPIDependsOnLoggerAsync(api, fileReader),
                                dependsOnAuthorizationServers = api.authenticationSettings != null && api.authenticationSettings.oAuth2 != null && api.authenticationSettings.oAuth2.authorizationServerId != null,
                                dependsOnBackends = await masterTemplateCreator.DetermineIfAPIDependsOnBackendAsync(api, fileReader)
                            });
                        }
                        else
                        {
                            await apiTemplateCreator.MergeApiTemplates(apiTemplateSet, apiTemplates);
                        }
                    }

                    if (!mergeApiTemplates)
                    {
                        // create the relevant info that will be needed to properly link to the api template(s) from the master template
                        apiInformation.Add(new LinkedMasterTemplateAPIInformation()
                        {
                            name = creatorConfig.baseFileName,
                            isSplit = true,
                            dependsOnGlobalServicePolicies = creatorConfig.policy != null,
                            dependsOnVersionSets = false,
                            dependsOnProducts = creatorConfig.products != null,
                            dependsOnTags = creatorConfig.tags != null,
                            dependsOnLoggers = creatorConfig.loggers != null,
                            dependsOnAuthorizationServers = creatorConfig.authorizationServers != null && creatorConfig.authorizationServers.Count > 0,
                            dependsOnBackends = creatorConfig.backends != null
                        });
                    }

                    Console.WriteLine("Creating tag template");
                    Console.WriteLine("------------------------------------------");
                    Template tagTemplate = creatorConfig.tags != null ? tagTemplateCreator.CreateTagTemplate(creatorConfig) : null;

                    // create parameters file
                    Template templateParameters = masterTemplateCreator.CreateMasterTemplateParameterValues(creatorConfig);

                    // write templates to outputLocation
                    if (creatorConfig.linked)
                    {
                        // create linked master template
                        Template masterTemplate = masterTemplateCreator.CreateLinkedMasterTemplate(creatorConfig, globalServicePolicyTemplate, apiVersionSetsTemplate, productsTemplate, propertyTemplate, loggersTemplate, backendsTemplate, authorizationServersTemplate, tagTemplate, apiInformation, fileNames, creatorConfig.apimServiceName, fileNameGenerator);
                        fileWriter.WriteJSONToFile(masterTemplate, String.Concat(creatorConfig.outputLocation, fileNames.linkedMaster));
                    }

                    foreach (Template apiTemplate in apiTemplates)
                    {
                        APITemplateResource apiResource = apiTemplate.resources.FirstOrDefault(resource => resource.type == ResourceTypeConstants.API) as APITemplateResource;
                        APIConfig providedAPIConfiguration = creatorConfig.apis.FirstOrDefault(api => string.Compare(apiResource.name, APITemplateCreator.MakeResourceName(api), true) == 0);

                        // if the api version is not null the api is split into multiple templates. If the template is split and the content value has been set, then the template is for a subsequent api
                        string apiFileName = fileNameGenerator.GenerateCreatorAPIFileName(mergeApiTemplates ? creatorConfig.baseFileName : providedAPIConfiguration.name,
                                                apiTemplateCreator.isSplitAPI(providedAPIConfiguration), apiResource.properties.value != null);
                        fileWriter.WriteJSONToFile(apiTemplate, String.Concat(creatorConfig.outputLocation, apiFileName));
                    }

                    if (globalServicePolicyTemplate != null)
                    {
                        fileWriter.WriteJSONToFile(globalServicePolicyTemplate, String.Concat(creatorConfig.outputLocation, fileNames.globalServicePolicy));
                    }
                    if (apiVersionSetsTemplate != null && !mergeApiTemplates)
                    {
                        fileWriter.WriteJSONToFile(apiVersionSetsTemplate, String.Concat(creatorConfig.outputLocation, fileNames.apiVersionSets));
                    }
                    if (productsTemplate != null)
                    {
                        fileWriter.WriteJSONToFile(productsTemplate, String.Concat(creatorConfig.outputLocation, fileNames.products));
                    }
                    if (propertyTemplate != null)
                    {
                        fileWriter.WriteJSONToFile(propertyTemplate, String.Concat(creatorConfig.outputLocation, fileNames.namedValues));
                    }
                    if (loggersTemplate != null)
                    {
                        fileWriter.WriteJSONToFile(loggersTemplate, String.Concat(creatorConfig.outputLocation, fileNames.loggers));
                    }
                    if (backendsTemplate != null)
                    {
                        fileWriter.WriteJSONToFile(backendsTemplate, String.Concat(creatorConfig.outputLocation, fileNames.backends));
                    }
                    if (authorizationServersTemplate != null)
                    {
                        fileWriter.WriteJSONToFile(authorizationServersTemplate, String.Concat(creatorConfig.outputLocation, fileNames.authorizationServers));
                    }
                    if (tagTemplate != null)
                    {
                        fileWriter.WriteJSONToFile(tagTemplate, String.Concat(creatorConfig.outputLocation, fileNames.tags));
                    }

                    // write parameters to outputLocation
                    fileWriter.WriteJSONToFile(templateParameters, String.Concat(creatorConfig.outputLocation, fileNames.parameters));
                    Console.WriteLine("Templates written to output location");
                }
                return 0;
            });
        }
    }
}