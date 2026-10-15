rootProject.name = "loans-service"

include("loans-api", "loans-kernel", "loans-calendar", "loans-rates", "loans-terms", "loans-schedule", "loans-disclosure", "loans-migration", "loans-application",
    "loans-architecture", "loans-acceptance")

// The implementation modules have no sources here (they come from -PimplDir or a model's code): a clean checkout has no
// directory for them, so they are made.
rootProject.children.forEach { it.projectDir.mkdirs() }
