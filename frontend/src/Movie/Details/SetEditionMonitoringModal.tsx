import React, { useCallback, useState } from 'react';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { ApiError } from 'Helpers/Hooks/useApiQuery';
import MovieEdition from 'Movie/MovieEdition';
import postEdition from 'Movie/postEdition';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

export interface MonitoringSelection {
  target: MovieEdition;
  movieMonitored: boolean;
}

interface SetEditionMonitoringModalProps {
  movieId: number;
  selection?: MonitoringSelection;
  onSelected(): void;
  onError(message: string): void;
  onClose(): void;
}

function SetEditionMonitoringModal({
  movieId,
  selection,
  onSelected,
  onError,
  onClose,
}: SetEditionMonitoringModalProps) {
  const [isSubmitting, setIsSubmitting] = useState(false);
  const onCancel = useCallback(() => {
    if (!isSubmitting) onClose();
  }, [isSubmitting, onClose]);
  const onConfirm = useCallback(async () => {
    if (isSubmitting || !selection) return;
    setIsSubmitting(true);
    try {
      await postEdition<MovieEdition>(
        `/movieedition/${selection.target.id}/monitoring`,
        {
          movieId,
          monitored: !selection.target.monitored,
          expectedMovieMonitored: selection.movieMonitored,
          expectedEditionMonitored: selection.target.monitored,
          expectedMovieFileId: selection.target.movieFileId ?? null,
          expectedQualityProfileId: selection.target.qualityProfileId,
          expectedRuleRevision: selection.target.ruleRevision,
          expectedName: selection.target.name,
        }
      );
      onSelected();
    } catch (requestError) {
      onError(
        requestError instanceof ApiError
          ? getErrorMessage(
              requestError,
              translate('EditionMonitoringFailed')
            ) || translate('EditionMonitoringFailed')
          : translate('EditionMonitoringFailed')
      );
    } finally {
      setIsSubmitting(false);
    }
  }, [isSubmitting, selection, movieId, onSelected, onError]);
  const label = translate(
    selection?.target.monitored ? 'UnmonitorEdition' : 'MonitorEdition'
  );
  return (
    <ConfirmModal
      isOpen={!!selection}
      title={label}
      isSpinning={isSubmitting}
      confirmLabel={label}
      cancelLabel={translate('Cancel')}
      message={
        <>
          <p>
            {translate(
              selection?.target.monitored
                ? 'UnmonitorEditionConfirm'
                : 'MonitorEditionConfirm',
              { edition: selection?.target.name ?? '' }
            )}
          </p>
          {!selection?.movieMonitored && !selection?.target.monitored ? (
            <p>{translate('EditionMonitoringMasterOff')}</p>
          ) : null}
        </>
      }
      onConfirm={onConfirm}
      onCancel={onCancel}
    />
  );
}

export default SetEditionMonitoringModal;
