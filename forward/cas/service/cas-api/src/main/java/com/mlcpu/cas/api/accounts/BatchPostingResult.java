package com.mlcpu.cas.api.accounts;

import java.util.List;

/** The batch's outcomes in input order; processed = posted + rejected. */
public record BatchPostingResult(int processed, int posted, int rejected, List<PostingOutcome> outcomes) {
    public BatchPostingResult {
        outcomes = List.copyOf(outcomes);
    }
}
