// Reads a compiled fixed API (records, enums, sealed outcomes, interfaces) into mlcpu.api-model.v1 JSON: the api-first input of
// the template pipeline's service model (GemmaJavaTemplates.md, ApiImporter). Reflection on the classes the API's own build
// compiled (with -parameters), so the model is exactly what the code declares. No dependencies.
plugins { application }
java { toolchain { languageVersion.set(JavaLanguageVersion.of(21)) } }
application { mainClass.set("com.mlcpu.forward.ApiImporter") }
tasks.withType<JavaCompile>().configureEach { options.encoding = "UTF-8" }
