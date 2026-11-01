import static org.junit.platform.engine.discovery.DiscoverySelectors.selectPackage;

import java.io.PrintWriter;
import org.junit.platform.launcher.Launcher;
import org.junit.platform.launcher.LauncherDiscoveryRequest;
import org.junit.platform.launcher.core.LauncherDiscoveryRequestBuilder;
import org.junit.platform.launcher.core.LauncherFactory;
import org.junit.platform.launcher.listeners.SummaryGeneratingListener;
import org.junit.platform.launcher.listeners.TestExecutionSummary;

/** Runs the auditor's JUnit 5 tests through the JUnit Platform launcher (no Gradle): see jobol-test.sh. */
public final class JUnitMain {
    public static void main(String[] args) {
        LauncherDiscoveryRequest request = LauncherDiscoveryRequestBuilder.request()
            .selectors(selectPackage(args.length > 0 ? args[0] : "com.mlcpu.jobol")).build();
        Launcher launcher = LauncherFactory.create();
        SummaryGeneratingListener listener = new SummaryGeneratingListener();
        launcher.execute(request, listener);
        TestExecutionSummary s = listener.getSummary();
        PrintWriter out = new PrintWriter(System.out, true);
        s.printFailuresTo(out, 25);
        s.printTo(out);
        System.exit(s.getTotalFailureCount() == 0 && s.getTestsSucceededCount() > 0 ? 0 : 1);
    }
}
