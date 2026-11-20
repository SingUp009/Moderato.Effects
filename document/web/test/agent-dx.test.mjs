import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

test('Agent DX build publishes matching counts and the frozen guide download', () => {
    const built = new URL('../dist/agent-dx/', import.meta.url);
    const report = JSON.parse(readFileSync(new URL('results.json', built), 'utf8'));
    const page = readFileSync(new URL('index.html', built), 'utf8');
    const archive = readFileSync(new URL('guide-snapshots.zip', built));

    assert.equal(archive.subarray(0, 2).toString(), 'PK');
    assert.ok(page.includes(report.guide.sha256));
    assert.ok(page.includes('/agent-dx/guide-snapshots.zip'));
    if (report.holdout.status === 'complete') {
        const completed = report.holdout.results;
        assert.equal(completed.observed, completed.planned);
        assert.equal(completed.baselineSha256, report.guide.baselineSha256);
        assert.equal(completed.frozenSha256, report.guide.sha256);
        assert.ok(page.includes('95% Wilson interval'));
        for (const model of completed.models) {
            const modelRows = completed.rows.filter(row => row.model === model.model);
            for (const condition of ['baseline', 'frozen']) {
                const rows = modelRows.filter(row => row.condition === condition);
                assert.equal(rows.reduce((sum, row) => sum + row.finalPass, 0), model[condition]);
                assert.ok(rows.every(row => row.observed === row.planned && row.finalWilson95?.length === 2));
            }
        }
    }
    for (const experiment of report.experiments) {
        assert.ok(experiment.rows.length > 0);
        for (const row of experiment.rows) {
            assert.ok(row.planned > 0);
            assert.ok(row.firstBuild >= 0 && row.firstBuild <= row.planned);
            assert.ok(row.finalPass >= 0 && row.finalPass <= row.planned);
        }
    }
});
