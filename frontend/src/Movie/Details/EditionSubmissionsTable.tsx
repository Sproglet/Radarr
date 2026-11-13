import React, { useCallback, useState } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import useApiQuery, { ApiError } from 'Helpers/Hooks/useApiQuery';
import { kinds } from 'Helpers/Props';
import postEdition from 'Movie/postEdition';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

interface Submission {
  operationId: string;
  movieEditionId: number;
  editionName: string;
  releaseTitle: string;
  downloadClientName: string;
  downloadId?: string;
  state: 'prepared' | 'accepted' | 'recorded' | 'settled';
  canReconcile: boolean;
  message: string;
}

const columns = [
  { name: 'edition', label: () => translate('Edition'), isVisible: true },
  { name: 'release', label: () => translate('Release'), isVisible: true },
  { name: 'client', label: () => translate('DownloadClient'), isVisible: true },
  { name: 'status', label: () => translate('Status'), isVisible: true },
  { name: 'actions', label: () => translate('Actions'), isVisible: true },
];

const statuses = {
  prepared: 'EditionSubmissionPrepared',
  accepted: 'EditionSubmissionAccepted',
  recorded: 'EditionSubmissionRecorded',
  settled: 'EditionSubmissionSettled',
};

function SubmissionRow({
  item,
  isDisabled,
  onSelect,
}: {
  item: Submission;
  isDisabled: boolean;
  onSelect(item: Submission): void;
}) {
  const onPress = useCallback(() => onSelect(item), [item, onSelect]);
  return (
    <TableRow>
      <TableRowCell>{item.editionName}</TableRowCell>
      <TableRowCell>{item.releaseTitle}</TableRowCell>
      <TableRowCell>{item.downloadClientName}</TableRowCell>
      <TableRowCell>
        <p>{translate(statuses[item.state] ?? 'EditionSubmissionPrepared')}</p>
        {item.message}
      </TableRowCell>
      <TableRowCell>
        {item.canReconcile ? (
          <Button isDisabled={isDisabled} onPress={onPress}>
            {translate('RepairEditionSubmission')}
          </Button>
        ) : null}
      </TableRowCell>
    </TableRow>
  );
}

function EditionSubmissionsTable({ movieId }: { movieId: number }) {
  const { data, error, isFetching, refetch } = useApiQuery<Submission[]>({
    path: `/movieedition/submission?movieId=${movieId}`,
    queryOptions: { staleTime: 0, refetchOnMount: 'always' },
  });
  const [selection, setSelection] = useState<Submission>();
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [failure, setFailure] = useState<string>();
  const onSelect = useCallback((item: Submission) => {
    setFailure(undefined);
    setSelection(item);
  }, []);
  const onCancel = useCallback(() => {
    if (!isSubmitting) setSelection(undefined);
  }, [isSubmitting]);
  const onConfirm = useCallback(async () => {
    if (!selection || isSubmitting) return;
    setIsSubmitting(true);
    try {
      await postEdition<Submission>(
        `/movieedition/submission/${selection.operationId}/reconcile`,
        {
          movieId,
          movieEditionId: selection.movieEditionId,
          expectedState: selection.state,
          expectedDownloadId: selection.downloadId,
        }
      );
    } catch (requestError) {
      const fallback = translate('EditionSubmissionRepairFailed');
      setFailure(
        requestError instanceof ApiError
          ? getErrorMessage(requestError, fallback) || fallback
          : fallback
      );
    } finally {
      setSelection(undefined);
      setIsSubmitting(false);
      refetch();
    }
  }, [selection, isSubmitting, movieId, refetch]);

  if (error) {
    return (
      <Alert kind={kinds.DANGER}>
        {translate('EditionSubmissionLoadFailed')}
      </Alert>
    );
  }
  if (!data?.length) return null;
  return (
    <>
      <h3>{translate('EditionSubmissionReview')}</h3>
      <p>{translate('EditionSubmissionReviewHelp')}</p>
      {failure ? <Alert kind={kinds.DANGER}>{failure}</Alert> : null}
      <Table columns={columns}>
        <TableBody>
          {data.map((item) => (
            <SubmissionRow
              key={item.operationId}
              item={item}
              isDisabled={isFetching || isSubmitting}
              onSelect={onSelect}
            />
          ))}
        </TableBody>
      </Table>
      <ConfirmModal
        isOpen={!!selection}
        title={translate('RepairEditionSubmission')}
        confirmLabel={translate('RepairEditionSubmission')}
        cancelLabel={translate('Cancel')}
        isSpinning={isSubmitting}
        message={translate('RepairEditionSubmissionConfirm', {
          edition: selection?.editionName ?? '',
        })}
        onConfirm={onConfirm}
        onCancel={onCancel}
      />
    </>
  );
}

export default EditionSubmissionsTable;
