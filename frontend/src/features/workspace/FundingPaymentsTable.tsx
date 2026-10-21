import type { FundingPayment } from '@/api/activity'
import type { BrokerAccount } from '@/api/workspace'
import { amount } from '@/lib/format'
import { resultSign } from './activity-format'
import { Time } from '@/components/ui/time'

export function FundingPaymentsTable({ payments, accounts }: { payments: FundingPayment[]; accounts?: BrokerAccount[] }) {
  return <div className="workspace-table-wrap"><table className="workspace-table funding-payments-table">
    <caption className="sr-only">Settled funding payments</caption>
    <thead><tr>{accounts && <th>Account / instrument</th>}<th>Settlement</th><th>Asset</th><th className="numeric">Payment</th></tr></thead>
    <tbody>{payments.map(payment => <tr key={payment.id}>
      {accounts && <td>{accounts.find(account => account.id === payment.accountId)?.name ?? 'Account'} · {payment.contractId}</td>}
      <td title={`Source ${payment.sourceId} · Native contract ${payment.venueContractId ?? payment.contractId}${payment.occurredAtUnixNanoseconds === null ? '' : ` · Exact epoch nanoseconds ${payment.occurredAtUnixNanoseconds}`}`}>
        <Time at={payment.occurredAtUtc} style="datetime-seconds" />
      </td>
      <td>{payment.asset}</td>
      <td className={`numeric operation-result ${resultSign(payment.amount)}`} title={payment.amount}>
        {amount(payment.amount)} <small>{resultSign(payment.amount) === 'positive' ? 'Received' : resultSign(payment.amount) === 'negative' ? 'Paid' : 'Zero settlement'}</small>
      </td>
    </tr>)}</tbody>
  </table></div>
}
