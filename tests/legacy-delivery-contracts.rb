require 'json'
require 'open3'
require 'tmpdir'
require 'minitest/autorun'

class LegacyDeliveryContracts < Minitest::Test
  ROOT = File.expand_path('..', __dir__)

  def test_uci_change_includes_its_real_dependents_without_database_work
    # Exercise the existing planner CLI; this test adds no Python implementation.
    out, err, status = Open3.capture3('python3', 'scripts/ci-impact-plan.py', '--changed-file', 'app/Laplace.Chess.Uci/Program.cs', chdir: ROOT)
    assert status.success?, err
    plan = JSON.parse(out)
    assert_equal %w[managed uci], plan.fetch('components')
    assert_equal ['managed'], plan.fetch('build_components')
    assert_equal %w[Laplace.Chess.Tests Laplace.Chess.Uci Laplace.Endpoints.Engines.Tests Laplace.Endpoints.Engines].map { |name| "app/#{name}/#{name}.csproj" }, plan.fetch('managed_build_projects')
    assert_equal %w[Laplace.Chess.Tests Laplace.Endpoints.Engines.Tests].map { |name| "app/#{name}/#{name}.csproj" }, plan.fetch('managed_test_projects')
    assert_equal %w[managed-dev uci-dev], plan.fetch('dev_suites')
    assert_empty plan.fetch('db_suites')
    assert_empty plan.fetch('live_suites')
    assert_equal ['publish'], plan.fetch('delivery_actions')
    assert_equal 'uci', plan.fetch('publish_scope')
    assert_equal false, plan.fetch('full_qualification')
  end

  def test_native_filter_reaches_ctest_unchanged
    common = File.read(File.join(ROOT, 'scripts/test-suites/common.sh'))
    native = File.read(File.join(ROOT, 'scripts/test-suites/native-dev.sh'))
    functions = [common[/^run_ctest\(\) \{.*?^\}/m], native[/^run_native_dev\(\) \{.*?^\}/m]]
    refute functions.any?(&:nil?)
    Dir.mktmpdir('native-filter-', ENV.fetch('TMPDIR')) do |dir|
      # Stub only the external tool boundary; exercise the shipped argument assembly.
      File.write(File.join(dir, 'python3'), "#!/bin/sh\nprintf '%s\\0' \"$@\" > \"$CALL_LOG\"\n")
      File.chmod(0755, File.join(dir, 'python3'))
      filter = 'LaplaceDynamicsProcrustes\\.RecoversScale'
      env = {'ROOT'=>ROOT, 'PATH'=>"#{dir}:#{ENV.fetch('PATH')}", 'CALL_LOG'=>File.join(dir,'args'), 'CTEST_PARALLEL_LEVEL'=>'1', 'LAPLACE_NATIVE_TEST_FILTER'=>filter}
      out, status = Open3.capture2e(env, 'bash', '-c', "set -euo pipefail\n#{functions.join("\n")}\nrun_native_dev")
      assert status.success?, out
      args = File.binread(File.join(dir,'args')).split("\0")
      assert_equal filter, args.fetch(args.index('-R') + 1)
      assert_equal 'regress', args.fetch(args.index('-LE') + 1)
      refute_includes args, 'regress_laplace_substrate'
    end
  end
end
