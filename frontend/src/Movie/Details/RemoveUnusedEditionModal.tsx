import React, { useCallback, useState } from 'react';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { ApiError } from 'Helpers/Hooks/useApiQuery';
import MovieEdition from 'Movie/MovieEdition';
import postEdition from 'Movie/postEdition';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

export interface RemovalSelection {
  target: MovieEdition;
  movieMonitored: boolean;
  expectedPrimaryEditionId: number;
}

function RemoveUnusedEditionModal({
  movieId,
  selection,
  onRemoved,
  onError,
  onClose,
}: {
  movieId: number;
  selection?: RemovalSelection;
  onRemoved(): void;
  onError(message: string): void;
  onClose(): void;
}) {
  const [isSubmitting, setIsSubmitting] = useState(false);
  const onCancel = useCallback(() => {
    if (!isSubmitting) onClose();
  }, [isSubmitting, onClose]);
  const onConfirm = useCallback(async () => {
    if (!selection || isSubmitting) return;
    setIsSubmitting(true);
    try {
      await postEdition<{ id: number; movieId: number }>(
        `/movieedition/${selection.target.id}/remove`,
        {
          movieId,
          expectedPrimaryEditionId: selection.expectedPrimaryEditionId,
          expectedMovieMonitored: selection.movieMonitored,
          expectedEditionMonitored: selection.target.monitored,
          expectedMovieFileId: selection.target.movieFileId ?? null,
          expectedQualityProfileId: selection.target.qualityProfileId,
          expectedRuleRevision: selection.target.ruleRevision,
          expectedName: selection.target.name,
        }
      );
      onRemoved();
    } catch (requestError) {
      const fallback = translate('EditionRemovalFailed');
      onError(
        requestError instanceof ApiError
          ? getErrorMessage(requestError, fallback) || fallback
          : fallback
      );
    } finally {
      setIsSubmitting(false);
    }
  }, [selection, isSubmitting, movieId, onRemoved, onError]);
  return (
    <ConfirmModal
      isOpen={!!selection}
      title={translate('RemoveUnusedEdition')}
      confirmLabel={translate('RemoveUnusedEdition')}
      cancelLabel={translate('Cancel')}
      isSpinning={isSubmitting}
      message={translate('RemoveUnusedEditionConfirm', {
        edition: selection?.target.name ?? '',
      })}
      onConfirm={onConfirm}
      onCancel={onCancel}
    />
  );
}

export default RemoveUnusedEditionModal;
